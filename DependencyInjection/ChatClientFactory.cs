using Anthropic;
using ChatAI.Interfaces;
using ChatAI.Models;
using ChatAI.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace ChatAI.DependencyInjection;

public static class ChatClientExtensions
{
    public static IServiceCollection AddAIChatServices(this IServiceCollection services)
    {
        services.AddOptions<AISettings>()
            .BindConfiguration("AISettings")
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AISettings>>().Value;

            var inner = settings.Provider switch
            {
                "OpenAI" => new OpenAIClient(settings.OpenAI.ApiKey)
                                .GetChatClient(settings.OpenAI.Model)
                                .AsIChatClient(),

                "Anthropic" => new AnthropicClient { ApiKey = settings.Anthropic.ApiKey }
                                .AsIChatClient(settings.Anthropic.Model),

                _ => throw new InvalidOperationException(
                    $"Unknown AI provider: '{settings.Provider}'. Valid values: Anthropic, OpenAI")
            };

            return new ChatClientBuilder(inner)
                .UseFunctionInvocation()
                .Build();
        });

        services.AddSingleton<IDatabaseService, DatabaseService>();
        services.AddSingleton<DatabaseSeeder>();
        services.AddSingleton<DatabaseTools>();

        services.AddScoped<IChatService, AIChatService>();

        return services;
    }
}
