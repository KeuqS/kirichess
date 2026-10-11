using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Sentry;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Puts every SignalR hub invocation inside a log scope carrying the hub method, the caller's user
/// id and (when the method takes one) the game id, so a failure in a real-time method is traceable
/// to the game it broke. Without this, a hub exception is logged with no correlation at all, the
/// exact blindness Phase 6d exists to remove.
///
/// <see cref="HubException"/> is normal control flow here (the hubs use it for "Game not found",
/// "Not your turn", and every other rejection), so it is logged at Information and never reported
/// as an error. Anything else is a genuine defect: it is logged, counted, and, if a Sentry DSN is
/// configured, reported, then rethrown unchanged so the client still gets the failure.
/// </summary>
public sealed class HubObservabilityFilter : IHubFilter
{
    private readonly ILogger<HubObservabilityFilter> _logger;
    private readonly KiriChessMetrics _metrics;

    public HubObservabilityFilter(ILogger<HubObservabilityFilter> logger, KiriChessMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var method = invocationContext.HubMethodName;
        var userId = invocationContext.Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        // Every game-scoped hub method takes the game id as its first argument (see GameHub); the
        // queue methods do not, and simply get no GameId in scope.
        var gameId = invocationContext.HubMethodArguments.Count > 0
            && invocationContext.HubMethodArguments[0] is string first
            && Guid.TryParse(first, out var parsed)
                ? parsed.ToString()
                : null;

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["HubMethod"] = method,
            ["UserId"] = userId,
            ["GameId"] = gameId,
            ["ConnectionId"] = invocationContext.Context.ConnectionId
        });

        try
        {
            return await next(invocationContext);
        }
        catch (HubException ex)
        {
            _logger.LogInformation("Hub call {HubMethod} rejected: {Reason}", method, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _metrics.HubError(method);
            _logger.LogError(ex, "Unhandled exception in hub method {HubMethod}", method);
            // A no-op when no Sentry DSN was configured, SentrySdk.CaptureException is safe to call
            // unconditionally because the SDK was simply never initialised in that case (Phase 6d.2).
            SentrySdk.CaptureException(ex);
            throw;
        }
    }
}
