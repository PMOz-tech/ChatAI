namespace ChatAI.Models
{
    public class AISettings
    {
        public string Provider { get; set; } = "Anthropic";
        public AnthropicSettings Anthropic { get; set; } = new();
        public OpenAISettings OpenAI { get; set; } = new();
        public ResilienceSettings Resilience { get; set; } = new();
    }

    public class AnthropicSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "claude-sonnet-4-20250514";
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 1.0f;
        public string SystemPrompt { get; set; } = string.Empty;
    }

    public class OpenAISettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "gpt-4o";
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 1.0f;
    }

    public class ResilienceSettings
    {
        public int MaxRetryAttempts { get; set; } = 3;
        public int CircuitBreakerBreakSeconds { get; set; } = 30;
        public int RateLimitPermitLimit { get; set; } = 60;
        public int RateLimitWindowSeconds { get; set; } = 60;
    }
}
