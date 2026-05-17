using Anthropic;
using Anthropic.Models.Beta.Sessions.Events;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ChatAI.Services
{
    public class AnthropicChatClient
    {
        private readonly IChatClient _client;

        public AnthropicChatClient(AnthropicClient client)
        {
            _client = client.AsIChatClient("claude-sonnet-4-20250514");
        }

        public async Task<string> GetResponseAsync(
            ChatMessage message,
            CancellationToken cancellationToken = default)
        {
            var response = await _client.GetResponseAsync(
                [message],
                cancellationToken: cancellationToken
            );

            var result = new System.Text.StringBuilder();
            var messages = new List<ChatMessage> { message };
            await foreach (var chunk in _client.GetStreamingResponseAsync(messages))
            {
                Console.Write(chunk.Text);
                result.Append(chunk.Text);
            }

            return result.ToString();
            // return new ChatResponse(new ChatMessage(ChatRole.User, response.Text));
        }
    }
}
