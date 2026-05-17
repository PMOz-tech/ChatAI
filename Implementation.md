# ChatAI — Implementation Reference

## Overview

ChatAI is an ASP.NET Core 10 Web API that exposes a production-ready chat AI endpoint. It abstracts over multiple AI providers
(Anthropic Claude, OpenAI GPT) through a single interface, with full streaming support, structured logging, and automatic resilience built in.

---

## Architecture

```
HTTP Request
     │
     ▼
┌─────────────────────┐
│   ChatController    │  ← Validates input, handles error → HTTP status mapping
└────────┬────────────┘
         │ IChatService
         ▼
┌─────────────────────┐
│   AIChatService     │  ← Builds message list, applies ChatOptions, wraps in Polly
└────────┬────────────┘
         │ ResiliencePipeline("ai-chat")
         ▼
┌─────────────────────────────────────┐
│  Polly Pipeline                     │
│  [Rate Limiter] → [Circuit Breaker] │  ← Guards every call to the AI provider
│  → [Retry]                          │
└────────┬────────────────────────────┘
         │ IChatClient (MEAI)
         ▼
┌─────────────────────┐
│  AI Provider        │  ← Anthropic or OpenAI, selected at startup from config
│  (Claude / GPT)     │
└─────────────────────┘
```

### Request Flow

1. `ChatController` receives a `POST /api/chat` or `POST /api/chat/stream` request.
2. It delegates to `IChatService` (injected as `AIChatService`).
3. `AIChatService` builds the message list — system prompt first, then conversation history, then the new user message.
4. Every AI call is wrapped in the named Polly pipeline `"ai-chat"`, which enforces rate limits, trips a circuit breaker on sustained failures, and retries transient errors with exponential backoff.
5. The underlying `IChatClient` (MEAI abstraction) routes the call to the configured provider.
6. For streaming, the response chunks are written to `HttpResponse` directly as Server-Sent Events (SSE).

---

## Project Structure

```
ChatAI/
├── Controllers/
│   └── ChatController.cs          ← POST /api/chat and /api/chat/stream
├── DependencyInjection/
│   └── ChatClientFactory.cs       ← IServiceCollection extension: AddAIChatServices()
├── Interfaces/
│   └── IChatService.cs            ← Service contract
├── Models/
│   ├── AISettings.cs              ← Strongly-typed configuration (bound from appsettings)
│   ├── ChatRequest.cs             ← Request DTO
│   └── ChatResponse.cs            ← Response DTO
├── Services/
│   └── AIChatService.cs           ← Core logic: message building, Polly, IChatClient
├── appsettings.json               ← Non-secret config: model names, Serilog, resilience settings
├── appsettings.Development.json   ← Debug-level logging override
└── ChatAI.http                    ← Manual test requests (VS Code REST Client / Rider)
```

---

## Libraries

### Microsoft.Extensions.AI (MEAI) `v10.5.2`

**What it is:** Microsoft's official abstraction layer for AI services. Defines `IChatClient` — a single interface that any AI provider can implement.

**Why it's used here:**
- Code in `AIChatService` talks only to `IChatClient`. Swapping providers (Anthropic → OpenAI) requires changing one config value, not rewriting service code.
- `GetResponseAsync` returns a `ChatResponse` with text, model ID, and usage stats.
- `GetStreamingResponseAsync` returns `IAsyncEnumerable<ChatResponseUpdate>` — native async streaming with cancellation support.

**Key types used:**
| Type | Purpose |
|---|---|
| `IChatClient` | Provider-agnostic chat interface |
| `ChatMessage` | A single conversation turn with a `ChatRole` (System/User/Assistant) |
| `ChatOptions` | Per-request settings: model ID, max tokens, temperature |
| `ChatRole` | Enum-like struct: `System`, `User`, `Assistant` |

---

### Anthropic SDK `v12.20.0`

**What it is:** The official .NET SDK for Anthropic's Claude API.

**Why it's used here:** It provides the `AnthropicClient` class. Calling `.AsIChatClient(modelId)` on it wraps the client in the MEAI `IChatClient` interface — so all provider-specific code lives in `ChatClientFactory.cs` only.

**Key usage:**
```csharp
new AnthropicClient { ApiKey = settings.Anthropic.ApiKey }
    .AsIChatClient(settings.Anthropic.Model)
```

---

### Microsoft.Extensions.AI.OpenAI `v10.5.2`

**What it is:** The MEAI provider adapter for OpenAI's API.

**Why it's used here:** Same pattern as Anthropic — wraps `OpenAIClient` in `IChatClient` so the service layer sees no difference between providers.

**Key usage:**
```csharp
new OpenAIClient(settings.OpenAI.ApiKey)
    .GetChatClient(settings.OpenAI.Model)
    .AsIChatClient()
```

---

### Microsoft.Extensions.Resilience `v10.6.0` (Polly v8)

**What it is:** Microsoft's official DI-integrated wrapper around [Polly v8](https://github.com/App-vNext/Polly). Polly is the industry-standard .NET resilience library. Version 8 is a complete rewrite — it uses a unified `ResiliencePipeline` instead of the old policy classes.

**Why it's used here:** AI API calls are inherently unreliable — rate limits, transient network errors, and provider outages are common. Polly wraps every call with three protection layers.

**The `"ai-chat"` pipeline (innermost to outermost):**

#### Layer 1: Rate Limiter
```
SlidingWindowRateLimiter(60 permits / 60s window, 6 segments)
```
- Prevents your own app from hammering the AI API too fast.
- Rejects immediately with `RateLimiterRejectedException` when the window is full — no queuing.
- The controller maps this to HTTP `429 Too Many Requests`.

#### Layer 2: Circuit Breaker
```
FailureRatio=0.5, MinimumThroughput=5, BreakDuration=30s
```
- Monitors the failure rate over a rolling window.
- If ≥50% of the last 5+ calls fail, it trips open and stops calling the AI API entirely for 30 seconds.
- This prevents cascading failures and gives the downstream service time to recover.
- Logs OPENED / HALF-OPEN / CLOSED state changes for observability.
- The controller maps `BrokenCircuitException` to HTTP `503 Service Unavailable`.

#### Layer 3: Retry
```
MaxRetryAttempts=3, ExponentialBackoff, UseJitter=true, BaseDelay=500ms
```
- Retries on `HttpRequestException`, `TaskCanceledException`, and `TimeoutException`.
- Exponential backoff with jitter: ~500ms, ~1s, ~2s (randomised to prevent thundering herd).
- Each retry attempt re-enters the circuit breaker and rate limiter — so a retry never fires when the circuit is open.
- Logs the attempt number, delay, and exception message on each retry.

**Execution order when calling the AI:**
```
Request → Retry wraps [Circuit Breaker wraps [Rate Limiter wraps [AI call]]]
```

**Key DI types:**
| Type | Purpose |
|---|---|
| `AddResiliencePipeline(name, factory)` | Registers a named pipeline in DI |
| `ResiliencePipelineProvider<string>` | Injected into `AIChatService` to resolve the named pipeline |
| `ResiliencePipeline` | The actual pipeline — call `ExecuteAsync(delegate)` to protect a call |

---

### Serilog.AspNetCore `v9.0.0`

**What it is:** Structured logging framework. Unlike the default ASP.NET Core `ILogger` (which writes plain text), Serilog writes structured JSON logs where every field (`RequestMethod`, `StatusCode`, `Elapsed`) is a searchable property — not just a string.

**Why it's used here:**
- In production, structured logs are queryable in tools like Seq, Datadog, ELK stack, Application Insights.
- The request logging middleware adds one clean log line per HTTP request with timing, path, and status code.

**Configuration (appsettings.json):**
```json
"Serilog": {
  "MinimumLevel": { "Default": "Information" },
  "WriteTo": [
    { "Name": "Console" },
    { "Name": "File", "Args": { "path": "logs/chatai-.log", "rollingInterval": "Day" } }
  ]
}
```

**Two-phase setup in Program.cs:**

1. **Bootstrap logger** — created before the host builds, so startup errors are captured:
   ```csharp
   Log.Logger = new LoggerConfiguration()...CreateBootstrapLogger();
   ```

2. **Full logger** — replaces bootstrap after host is configured, reads from `appsettings.json`:
   ```csharp
   builder.Host.UseSerilog((ctx, svc, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));
   ```

**Sinks used:**
| Sink | Purpose |
|---|---|
| `Serilog.Sinks.Console` | Coloured console output during development |
| `Serilog.Sinks.File` | Rolling daily files at `logs/chatai-YYYYMMDD.log`, 7-day retention |

---

### Microsoft.AspNetCore.OpenApi `v10.0.2`

**What it is:** .NET 10's built-in OpenAPI document generator. Replaces Swashbuckle's spec generation half.

**Why it's used here:** Generates a machine-readable `/openapi/v1.json` document from:
- Controller route attributes
- `[ProducesResponseType]` attributes on actions
- XML doc comments (`<summary>`, `<param>`, `<returns>`) from the compiled `.xml` file (enabled via `<GenerateDocumentationFile>true</GenerateDocumentationFile>`)

**Note:** This package generates the spec only — it has no UI. Scalar provides the UI.

---

### Scalar.AspNetCore `v2.14.14`

**What it is:** A modern interactive API documentation UI that reads an OpenAPI spec and renders it as a Swagger-style explorer.

**Why Scalar instead of Swashbuckle's Swagger UI:**
- `Swashbuckle.AspNetCore` bundles its own spec generator (incompatible with .NET 10's built-in OpenAPI).
- Scalar is designed to work alongside `Microsoft.AspNetCore.OpenApi` — it reads the `/openapi/v1.json` spec that the built-in package produces.
- The UI is available at `GET /scalar/v1` (dev only).

**The `/scalar/v1` route:** `v1` is the document name, not a Scalar version. It maps to the OpenAPI document named `"v1"` registered by `AddOpenApi()`. If you want the path to be `/swagger`, configure it:
```csharp
app.MapScalarApiReference(options => options.WithEndpointPrefix("/swagger/{documentName}"));
```

---

## Configuration

All non-secret settings live in `appsettings.json`. API keys must be set via User Secrets (development) or environment variables (production) — they are never committed to source control.

### User Secrets setup (one-time)
```bash
dotnet user-secrets set "AISettings:Anthropic:ApiKey" "<your-anthropic-key>"
dotnet user-secrets set "AISettings:OpenAI:ApiKey" "<your-openai-key>"
```

### Switching providers
Change `AISettings:Provider` in `appsettings.json`:
```json
"AISettings": {
  "Provider": "OpenAI"   // or "Anthropic"
}
```

### Resilience tuning
All Polly settings are configuration-driven via `AISettings:Resilience`:
```json
"Resilience": {
  "MaxRetryAttempts": 3,
  "CircuitBreakerBreakSeconds": 30,
  "RateLimitPermitLimit": 60,
  "RateLimitWindowSeconds": 60
}
```

---

## API Reference

### `POST /api/chat`
Returns a complete JSON response.

**Request:**
```json
{
  "message": "What is the capital of France?",
  "history": null
}
```

**Response `200 OK`:**
```json
{
  "response": "The capital of France is Paris.",
  "model": "claude-sonnet-4-20250514",
  "tokensUsed": 42
}
```

**Error responses:** `400 Bad Request` (empty message) · `429 Too Many Requests` (rate limited) · `503 Service Unavailable` (circuit open)

---

### `POST /api/chat/stream`
Returns a Server-Sent Events stream.

**Request:** Same shape as `/api/chat`. Set `Accept: text/event-stream`.

**Response stream:**
```
event: message
data: The capital

event: message
data:  of France is Paris.

event: done
data:
```

On error, an `event: error` is sent with a short description before the connection closes.

---

## System Prompt

The system prompt is injected as the first `ChatRole.System` message before every request. Configure it in `appsettings.json`:
```json
"AISettings": {
  "Anthropic": {
    "SystemPrompt": "You are a helpful AI assistant."
  }
}
```

Leave it empty (`""`) to send no system prompt.
