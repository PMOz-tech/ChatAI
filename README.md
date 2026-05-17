ChatAI is an ASP.NET Core 10 Web API that exposes a production-ready chat AI endpoint. It abstracts over multiple AI providers
(Anthropic Claude, OpenAI GPT) through a single interface, with full streaming support, structured logging, and automatic resilience built in.

---

## Architecture

```
HTTP Request
     │
     ▼
┌─────────────────────┐
│   ChatController    │  ← Validates input, handles error → HTTP status mapping
└────────┬────────────┘
         │ IChatService
         ▼
┌─────────────────────┐
│   AIChatService     │  ← Builds message list, applies ChatOptions, wraps in Polly
└────────┬────────────┘
         │ ResiliencePipeline("ai-chat")
         ▼
┌─────────────────────────────────────┐
│  Polly Pipeline                     │
│  [Rate Limiter] → [Circuit Breaker] │  ← Guards every call to the AI provider
│  → [Retry]                          │
└────────┬────────────────────────────┘
         │ IChatClient (MEAI)
         ▼
┌─────────────────────┐
│  AI Provider        │  ← Anthropic or OpenAI, selected at startup from config
│  (Claude / GPT)     │
└─────────────────────┘
```

To get more info please read the implementation.MD
