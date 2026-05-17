using Microsoft.Extensions.AI;

namespace ChatAI.Models;

/// <summary>
/// Request body for chat endpoints.
/// </summary>
public class ChatRequest
{
    /// <summary>
    /// The user's message to send to the AI.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional prior conversation turns for multi-turn chat.
    /// Each item should have Role set to "user" or "assistant".
    /// </summary>
    public List<ChatMessage>? History { get; set; }
}
