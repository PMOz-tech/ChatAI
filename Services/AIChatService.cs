using Microsoft.Extensions.AI;

namespace ChatAI.Services
{
    public class AIChatService
    {
        private readonly IChatClient _client;

        public AIChatService(IChatClient client)
        {
            _client = client;
        }

        public async Task<string> GetResponseAsync(string prompt)
        {
            var response = await _client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)]
            );
            return response.Text;
        }
    }
}
