using ChatAI.DependencyInjection;
using ChatAI.Models;
using ChatAI.Services;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using System.Threading.RateLimiting;

// Bootstrap logger captures startup errors before the host is configured.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ChatAI host");

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ──────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());

    // ── AI services (IChatClient + IChatService via DI) ───────────────────────
    builder.Services.AddAIChatServices();

    // ── Polly resilience pipeline ─────────────────────────────────────────────
    // Execution order (innermost → outermost): rate limiter → circuit breaker → retry
    builder.Services.AddResiliencePipeline("ai-chat", (pipelineBuilder, context) =>
    {
        var resilience = context.ServiceProvider
            .GetRequiredService<IOptions<AISettings>>().Value.Resilience;

        // 1. Rate limiter — checked first, rejects immediately when limit exceeded
        pipelineBuilder.AddRateLimiter(new SlidingWindowRateLimiter(
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = resilience.RateLimitPermitLimit,
                Window = TimeSpan.FromSeconds(resilience.RateLimitWindowSeconds),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // 2. Circuit breaker — opens after sustained failures, prevents hammering
        pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerBreakSeconds),
            BreakDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerBreakSeconds),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>()
                .Handle<TimeoutException>(),
            OnOpened = _ =>
            {
                context.ServiceProvider
                    .GetRequiredService<ILogger<Program>>()
                    .LogWarning("Circuit breaker OPENED — AI service paused for {Duration}s",
                        resilience.CircuitBreakerBreakSeconds);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                context.ServiceProvider
                    .GetRequiredService<ILogger<Program>>()
                    .LogInformation("Circuit breaker CLOSED — AI service recovered");
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = _ =>
            {
                context.ServiceProvider
                    .GetRequiredService<ILogger<Program>>()
                    .LogInformation("Circuit breaker HALF-OPEN — probing AI service");
                return ValueTask.CompletedTask;
            }
        });

        // 3. Retry — outermost, drives the retry loop through circuit breaker and rate limiter
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = resilience.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(500),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>()
                .Handle<TimeoutException>(),
            OnRetry = retryArgs =>
            {
                context.ServiceProvider
                    .GetRequiredService<ILogger<Program>>()
                    .LogWarning(
                        "Retrying AI call. Attempt={Attempt}, DelayMs={Delay:0}, Reason={Reason}",
                        retryArgs.AttemptNumber + 1,
                        retryArgs.RetryDelay.TotalMilliseconds,
                        retryArgs.Outcome.Exception?.Message);
                return ValueTask.CompletedTask;
            }
        });
    });

    // ── MVC + OpenAPI ─────────────────────────────────────────────────────────
    builder.Services.AddControllers();

    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "ChatAI API";
            document.Info.Version = "v1";
            document.Info.Description =
                "Streaming chat AI endpoint powered by MEAI. " +
                "Supports Anthropic (Claude) and OpenAI (GPT) providers. " +
                "Resilience via Polly: retry, circuit breaker, and rate limiting.";
            return Task.CompletedTask;
        });
    });

    var app = builder.Build();

    // ── Database ──────────────────────────────────────────────────────────────
    await app.Services.GetRequiredService<DatabaseSeeder>().SeedAsync();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        // Raw OpenAPI spec: GET /openapi/v1.json
        app.MapOpenApi();

        // Interactive Swagger UI: GET /scalar/v1
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("ChatAI API");
            options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
        });
    }

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.0}ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? string.Empty);
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        };
    });

    app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
