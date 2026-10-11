using System.Security.Claims;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Adds the authenticated user id to the log scope for the rest of the request. The framework
/// already stamps every log line with a TraceId; this is the one field it cannot know and the one
/// that turns "a 500 happened" into "a 500 happened to this player".
/// </summary>
public sealed class UserScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UserScopeMiddleware> _logger;

    public UserScopeMiddleware(RequestDelegate next, ILogger<UserScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            await _next(context);
            return;
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["UserId"] = userId });
        await _next(context);
    }
}
