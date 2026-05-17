namespace ChatAI.Models;

/// <summary>
/// Response body returned by the non-streaming chat endpoint.
/// </summary>
public class ChatResponse
{
    /// <summary>The AI-generated reply text.</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>The model ID used to produce this response.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Total tokens consumed (prompt + completion). 0 when unavailable.</summary>
    public int TokensUsed { get; set; }
}
