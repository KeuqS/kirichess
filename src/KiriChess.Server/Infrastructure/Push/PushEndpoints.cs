using System.Security.Claims;
using KiriChess.Server.Auth;

namespace KiriChess.Server.Infrastructure.Push;

/// <summary>Minimal-API endpoints for web push (11b.1 follow-up): the public key, and subscribe/unsubscribe.</summary>
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous: the VAPID public key is handed to every browser as applicationServerKey the
        // instant it subscribes, so there is nothing to protect by gating it behind sign-in - only a
        // reason not to (a signed-out visitor should still be able to see whether push is even offered).
        app.MapGet("/api/push/vapid-public-key", (VapidOptions vapid) =>
            Results.Ok(new VapidPublicKeyDto(vapid.Enabled, vapid.Enabled ? vapid.PublicKey : null)))
        .Produces<VapidPublicKeyDto>(StatusCodes.Status200OK);

        var push = app.MapGroup("/api/push")
                      .RequireAuthorization()
                      .RequireRateLimiting(RateLimitPolicies.AccountManage);

        push.MapPost("/subscribe", async (
            PushSubscribeRequest req, ClaimsPrincipal caller, PushSubscriptionService subscriptions, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Endpoint) || string.IsNullOrWhiteSpace(req.P256dh) || string.IsNullOrWhiteSpace(req.Auth))
                return Results.BadRequest(new { error = "Endpoint, P256dh and Auth are all required." });

            // S-1-8. Before the store, not after: whatever is persisted here is a URL WebPushSender
            // will later POST to from inside the container, so refusing it is the only point at which
            // it costs nothing. 400 rather than a store-and-fail-later, and the route already
            // declares Status400BadRequest, so this moves no wire contract.
            if (!PushEndpointPolicy.IsAllowed(req.Endpoint))
                return Results.BadRequest(new { error = PushEndpointPolicy.RejectionMessage });

            await subscriptions.SubscribeAsync(me.Value, req.Endpoint, req.P256dh, req.Auth, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        push.MapPost("/unsubscribe", async (
            PushUnsubscribeRequest req, ClaimsPrincipal caller, PushSubscriptionService subscriptions, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            await subscriptions.UnsubscribeAsync(me.Value, req.Endpoint, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

}
