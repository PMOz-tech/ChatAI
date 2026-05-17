namespace ChatAI.Models;

/// <summary>
/// A single prior conversation turn sent by the client.
/// </summary>
public class ChatHistoryMessage
{
    /// <summary>The speaker role. Accepted values: "user", "assistant".</summary>
    public string Role { get; set; } = "user";

    /// <summary>The text content of this turn.</summary>
    public string Content { get; set; } = string.Empty;
}
