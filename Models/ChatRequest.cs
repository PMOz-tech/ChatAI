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
    /// Each item requires <c>role</c> ("user" or "assistant") and <c>content</c>.
    /// </summary>
    public List<ChatHistoryMessage>? History { get; set; }
}
