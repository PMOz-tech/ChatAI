using ChatAI.Models;
using Microsoft.Extensions.AI;
using Anthropic;
using OpenAI;
namespace ChatAI.DependencyInjection
{
    public static class ChatClientFactory
    {
        public static IChatClient Create(AISettings settings) =>
            settings.Provider switch
            {
                "OpenAI" => new OpenAIClient(settings.OpenAI.ApiKey)
                                .GetChatClient(settings.OpenAI.Model)
                                .AsIChatClient(),

                "Anthropic" => new AnthropicClient { ApiKey = settings.Anthropic.ApiKey }
                                .AsIChatClient(settings.Anthropic.Model),

                _ => throw new InvalidOperationException($"Unknown provider: {settings.Provider}")
            };
    }
}
