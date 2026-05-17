using ChatAI.Models;

namespace ChatAI.Interfaces;

/// <summary>
/// Abstraction over the AI chat provider. Supports both full-response and streaming modes.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Sends a chat request and waits for the complete response.
    /// </summary>
    Task<ChatResponse> GetResponseAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends a chat request and streams the response as an async sequence of text chunks.
    /// </summary>
    IAsyncEnumerable<string> GetStreamingResponseAsync(ChatRequest request, CancellationToken ct = default);
}
