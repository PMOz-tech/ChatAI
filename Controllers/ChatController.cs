using ChatAI.Interfaces;
using ChatAI.Models;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.RateLimiting;

namespace ChatAI.Controllers;

/// <summary>
/// Provides AI chat endpoints supporting both standard JSON responses and Server-Sent Events streaming.
/// </summary>
[ApiController]
[Route("api/chat")]
[Produces("application/json")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(IChatService chatService, ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    /// <summary>
    /// Send a message and receive a complete AI response.
    /// </summary>
    /// <param name="request">The chat message and optional prior conversation history.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The AI-generated reply, model ID, and token usage.</returns>
    /// <remarks>
    /// Supply <c>History</c> to maintain multi-turn conversation context.
    /// Each history item must have <c>Role</c> set to <c>"user"</c> or <c>"assistant"</c>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChatResponse>> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("Message cannot be empty.");

        try
        {
            var response = await _chatService.GetResponseAsync(request, ct);
            return Ok(response);
        }
        catch (RateLimiterRejectedException)
        {
            _logger.LogWarning("Rate limit exceeded on POST /api/chat");
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Too many requests. Please slow down and try again.");
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Circuit breaker open on POST /api/chat");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "AI service is temporarily unavailable. Please try again later.");
        }
    }

    /// <summary>
    /// Send a message and receive the AI response as a Server-Sent Events (SSE) stream.
    /// </summary>
    /// <param name="request">The chat message and optional prior conversation history.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Set the <c>Accept</c> header to <c>text/event-stream</c>.
    /// Each token chunk is delivered as <c>event: message</c>.
    /// A final <c>event: done</c> signals end of stream.
    /// On error, an <c>event: error</c> is sent with a description.
    /// </remarks>
    [HttpPost("stream")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task Stream([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Message cannot be empty.", ct);
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await foreach (var chunk in _chatService.GetStreamingResponseAsync(request, ct))
            {
                await Response.WriteAsync($"event: message\ndata: {chunk}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }

            await Response.WriteAsync("event: done\ndata: \n\n", ct);
        }
        catch (RateLimiterRejectedException)
        {
            _logger.LogWarning("Rate limit exceeded on POST /api/chat/stream");
            await Response.WriteAsync("event: error\ndata: Too many requests\n\n", ct);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Circuit breaker open on POST /api/chat/stream");
            await Response.WriteAsync("event: error\ndata: Service temporarily unavailable\n\n", ct);
        }

        await Response.Body.FlushAsync(ct);
    }
}
