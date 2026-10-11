using System.Collections.Concurrent;
using System.Diagnostics;
using KiriChess.Server.Auth;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// S-4-1: re-checks an API token while its hub connection is still open.
///
/// <b>The hole this closes.</b> SignalR authorizes a hub method against the
/// <c>ClaimsPrincipal</c> captured when the connection was established, never against a freshly
/// authenticated one. <c>ApiTokenPolicies.HubPlay</c> does re-run on every invocation, but it only
/// ever re-reads claims frozen at connect time, and <see cref="ApiTokenService.FindValidAsync"/> -
/// the one thing that consults <c>RevokedAt</c>, <c>ExpiresAt</c> and <c>User.DeletedAt</c> - is
/// never called again for the life of that socket. <c>AddAuthenticationSchemes</c> on the policy does
/// not change this: it is honoured by the HTTP authorization middleware, not by the hub dispatcher.
/// So a bot whose token the owner has just revoked, or whose account a moderator has just banned,
/// kept calling <c>JoinQueue</c>, <c>SubmitMove</c> and <c>Resign</c> for as long as it held the
/// socket open, which is exactly what a bot is built to do. That matters more on the hub than over
/// HTTP: <c>/api/v1</c> play is always unrated, while hub <c>JoinQueue</c> is the rated bot pool, so
/// the connection that survived revocation was the one that moves ratings.
///
/// <b>Why the periodic re-check and not a published revocation signal.</b> The alternative shape is
/// for <c>SanctionService</c> and <c>ApiTokenService</c> to publish the affected user id and for
/// something here to abort matching connections. That needs a cross-instance channel (Redis, which
/// <c>RedisOptions.Enabled</c> makes optional, so it would also need a single-instance fallback), a
/// registry of live <c>HubCallerContext</c>s to abort through - SignalR offers no "abort connection
/// by id" on <c>IHubContext</c> - and a fan-out path that has to be correct while a node is dying.
/// That is a piece of infrastructure, not a fix. This is the lesser shape the finding names, and it
/// needs none of it: one indexed read per API connection per interval, no Redis, no new channel, and
/// identical behaviour on one instance or ten because each instance re-checks the connections it
/// physically holds. What it costs is latency: a revoked token keeps working for at most
/// <c>ApiTokens:HubRevalidationSeconds</c>, where the signal would have been immediate.
///
/// <b>It rejects; it does not abort the socket.</b> <c>HubCallerContext.Abort()</c> races the
/// completion message carrying the reason, so a bot aborted mid-invocation sees "connection closed"
/// rather than being told its key is dead. A rejected invocation is deterministic and legible, and
/// every rated action goes through an invocation, so nothing is reachable on a socket left open. The
/// residue is a useless socket the client holds until it gives up, which <c>HubConnectionBudget</c>
/// already caps per IP.
/// </summary>
public sealed class ApiTokenHubRevalidationFilter : IHubFilter
{
    /// <summary>
    /// What a bot whose key stopped being valid is told. A <see cref="HubException"/> because that is
    /// the only exception type whose message ASP.NET forwards to a SignalR client; anything else
    /// arrives as "an unexpected error", which is the mystery this is trying not to create.
    /// </summary>
    public const string RevokedMessage =
        "This API token is no longer valid. Reconnect with a current token.";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ApiTokenHubRevalidationFilter> _logger;
    private readonly TimeSpan _interval;

    /// <summary>
    /// Per-connection verdict cache. Keyed by connection id, removed on disconnect, so it cannot grow
    /// past the live connection count.
    /// </summary>
    private readonly ConcurrentDictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);

    public ApiTokenHubRevalidationFilter(
        IConfiguration configuration, IServiceScopeFactory scopes, ILogger<ApiTokenHubRevalidationFilter> logger)
    {
        // Same reasoning as HubRateLimitFilter's constructor: SignalR resolves hub filters lazily, on
        // the first invocation that needs one, which is always well after the host finishes Build().
        // So this reads the fully composed configuration, including a WebApplicationFactory override,
        // rather than a builder-time snapshot.
        //
        // Thirty seconds is the trade in one number: a revoked key stays usable for up to that long,
        // and a bot holding one socket costs one indexed primary-key read every thirty seconds. Zero
        // means check on every invocation, which is what the tests use.
        var seconds = configuration.GetValue("ApiTokens:HubRevalidationSeconds", 30);
        _interval = TimeSpan.FromSeconds(Math.Max(0, seconds));
        _scopes = scopes;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext.Context;

        // Only API-token connections. A session JWT expires on its own (Jwt:ExpiryMinutes) and
        // docs/decisions/AUTH.md is explicit that a live access token's ride ends at the next
        // /refresh, which is a standing position and not this filter's to reverse. An API token has
        // no such expiry, which is what makes it the unbounded case.
        if (context.User?.FindFirst(ApiTokenClaims.TokenUse)?.Value != ApiTokenClaims.Api)
            return await next(invocationContext);

        if (!await StillValidAsync(context))
            throw new HubException(RevokedMessage);

        return await next(invocationContext);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        _verdicts.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }

    private async Task<bool> StillValidAsync(HubCallerContext context)
    {
        // Fail closed: a connection that claims token_use=api without a parseable token_id is not
        // something the ApiToken handler can produce, so there is no legitimate caller to protect.
        var raw = context.User?.FindFirst(ApiTokenClaims.TokenId)?.Value;
        if (!Guid.TryParse(raw, out var tokenId)) return false;

        var connectionId = context.ConnectionId;
        if (_verdicts.TryGetValue(connectionId, out var cached))
        {
            // A revoked token is never un-revoked (RevokedAt is a set-once column, and lifting a
            // sanction does not restore the keys the ban revoked), so a negative verdict is final and
            // costs no further reads however hard the dead bot keeps calling.
            if (!cached.Valid) return false;
            // Stopwatch, not UtcNow: this is an interval, and a wall clock can step. Same rule
            // .claude/rules/realtime.md states for game time, for the same reason.
            if (Stopwatch.GetElapsedTime(cached.CheckedAt) < _interval) return true;
        }

        bool valid;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            valid = await tokens.IsStillValidAsync(tokenId, context.ConnectionAborted);
        }
        catch (OperationCanceledException)
        {
            // The connection is going away underneath us; let the invocation fail on its own terms.
            return true;
        }
        catch (Exception ex)
        {
            // Degrade toward the pre-S-4-1 behaviour rather than toward disconnecting every bot: a
            // database blip must not end games in progress. The verdict is deliberately not cached,
            // so the next invocation retries instead of inheriting the blip for a whole interval.
            _logger.LogWarning(ex, "Could not re-validate API token {TokenId} on a live hub connection; allowing this invocation.", tokenId);
            return true;
        }

        _verdicts[connectionId] = new Verdict(valid, Stopwatch.GetTimestamp());
        return valid;
    }

    private readonly record struct Verdict(bool Valid, long CheckedAt);
}
