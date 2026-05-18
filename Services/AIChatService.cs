using System.Runtime.CompilerServices;
using ChatAI.Interfaces;
using ChatAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
// Alias required: both ChatAI.Models and Microsoft.Extensions.AI define ChatResponse
using ChatResponse = ChatAI.Models.ChatResponse;

namespace ChatAI.Services;

public class AIChatService : IChatService
{
    private readonly IChatClient _client;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<AIChatService> _logger;
    private readonly AISettings _settings;
    private readonly DatabaseTools _dbTools;

    public AIChatService(
        IChatClient client,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<AIChatService> logger,
        IOptions<AISettings> settings,
        DatabaseTools dbTools)
    {
        _client = client;
        _pipelineProvider = pipelineProvider;
        _logger = logger;
        _settings = settings.Value;
        _dbTools = dbTools;
    }

    public async Task<ChatResponse> GetResponseAsync(ChatRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Chat request received. MessageLength={Length}, HistoryCount={Count}",
            request.Message.Length,
            request.History?.Count ?? 0);

        var messages = BuildMessages(request);
        var options = BuildChatOptions();
        var pipeline = _pipelineProvider.GetPipeline("ai-chat");

        var response = await pipeline.ExecuteAsync(
            async token => await _client.GetResponseAsync(messages, options, token),
            ct);

        var tokensUsed = (int)(response.Usage?.TotalTokenCount ?? 0);
        var modelId = response.ModelId ?? _settings.Anthropic.Model;

        _logger.LogInformation(
            "Chat request completed. Model={Model}, TokensUsed={Tokens}",
            modelId, tokensUsed);

        return new ChatResponse
        {
            Response = response.Text,
            Model = modelId,
            TokensUsed = tokensUsed
        };
    }

    public async IAsyncEnumerable<string> GetStreamingResponseAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Streaming chat request received. MessageLength={Length}, HistoryCount={Count}",
            request.Message.Length,
            request.History?.Count ?? 0);

        var messages = BuildMessages(request);
        var options = BuildChatOptions();
        var pipeline = _pipelineProvider.GetPipeline("ai-chat");

        // Polly guards connection initiation. IAsyncEnumerable is lazy — the HTTP
        // handshake happens on first MoveNextAsync, which occurs inside ExecuteAsync.
        var stream = await pipeline.ExecuteAsync(
            token => ValueTask.FromResult(
                _client.GetStreamingResponseAsync(messages, options, token)),
            ct);

        var chunkCount = 0;
        await foreach (var update in stream.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunkCount++;
                yield return update.Text;
            }
        }

        _logger.LogInformation("Streaming chat request completed. ChunkCount={ChunkCount}", chunkCount);
    }

    private List<ChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(_settings.Anthropic.SystemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, _settings.Anthropic.SystemPrompt));

        if (request.History is { Count: > 0 })
            foreach (var h in request.History)
            {
                var role = h.Role.ToLowerInvariant() switch
                {
                    "assistant" => ChatRole.Assistant,
                    "system"    => ChatRole.System,
                    _           => ChatRole.User
                };
                messages.Add(new ChatMessage(role, h.Content));
            }

        messages.Add(new ChatMessage(ChatRole.User, request.Message));
        return messages;
    }

    private ChatOptions BuildChatOptions() => new()
    {
        ModelId = _settings.Anthropic.Model,
        MaxOutputTokens = _settings.Anthropic.MaxTokens,
        Temperature = _settings.Anthropic.Temperature,
        Tools = [.._dbTools.All]
    };
}
