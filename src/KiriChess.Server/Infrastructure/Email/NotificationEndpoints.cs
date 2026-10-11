using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// One-click unsubscribe (Phase 11b.1). Anonymous by design and required to be: the entire point of a
/// mail footer's unsubscribe link is that it works without asking the recipient to sign in first, the
/// same reasoning behind <c>/api/challenges/{token}</c>'s own anonymous GET. Every other mail
/// preference concern (reading/setting all categories while signed in) lives on
/// <c>AccountEndpoints</c>'s <c>/api/account/notifications</c> instead.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/notifications/unsubscribe", async (
            UnsubscribeRequest req, NotificationPreferenceService preferences, UnsubscribeTokenService tokens,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<NotificationCategory>(req.Category, ignoreCase: true, out var category))
                return Results.BadRequest(new { error = $"Unknown category '{req.Category}'." });

            if (!tokens.Verify(req.UserId, category, req.Token))
                return Results.Json(
                    new { error = "That unsubscribe link is not valid." },
                    statusCode: StatusCodes.Status403Forbidden);

            await preferences.SetSubscribedAsync(req.UserId, category, subscribed: false, ct);
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.Auth) // anonymous, token-guessing-shaped traffic
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);

        // M-5-2: RFC 8058's one-click unsubscribe. A mail client's native "Unsubscribe" button never
        // runs JavaScript and never sends a JSON body - it POSTs the List-Unsubscribe URL with either
        // no body at all or `List-Unsubscribe=One-Click` as application/x-www-form-urlencoded. The
        // route above binds a JSON UnsubscribeRequest and would 400 that request, which is exactly the
        // gap this finding named: "the header would be a promise the server breaks."
        //
        // A separate route rather than teaching the existing one to accept both shapes: changing
        // /api/notifications/unsubscribe's own request binding would change what
        // Microsoft.AspNetCore.OpenApi infers for it, and docs/api/openapi.json is a byte-for-byte
        // contract test (OpenApiContractTests.Committed_document_is_up_to_date) that only the export
        // tool may regenerate. ExcludeFromDescription keeps this route - deliberately undocumented,
        // the way StripeWebhookEndpoints already is - out of that document entirely, so the committed
        // contract is untouched by this addition.
        //
        // The URL a mail client actually receives is UnsubscribeLink.BuildOneClick's, built against
        // EmailOptions.ApiBaseUrl (the API's own public origin) rather than the frontend page: this
        // handler must be reachable with no page render and no CORS preflight, and it carries the
        // exact same userId/category/token triple as the frontend link.
        app.MapPost("/api/notifications/unsubscribe/one-click", async (
            HttpRequest request, NotificationPreferenceService preferences, UnsubscribeTokenService tokens,
            CancellationToken ct) =>
        {
            var query = request.Query;
            if (!Guid.TryParse(query["userId"], out var userId))
                return Results.BadRequest();
            if (!Enum.TryParse<NotificationCategory>(query["category"], ignoreCase: true, out var category))
                return Results.BadRequest();
            var token = query["token"].ToString();

            if (!tokens.Verify(userId, category, token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            await preferences.SetSubscribedAsync(userId, category, subscribed: false, ct);
            // 200, not 204: RFC 8058 does not require a body, but several mailbox providers' one-click
            // probes treat a 2xx-with-no-particular-body as "worked" more reliably than a bare 204 -
            // this route has no browser or SPA caller to keep a leaner response for.
            return Results.Ok();
        })
        // No .Accepts<T>() and no bound body parameter at all, deliberately: RFC 8058 allows a
        // bodyless POST, and the one field this handler would ever read from a body
        // ("List-Unsubscribe=One-Click") carries no information the query string doesn't already have.
        .RequireRateLimiting(RateLimitPolicies.Auth) // same anonymous, token-guessing-shaped traffic as the route above
        .ExcludeFromDescription();

        return app;
    }
}

/// <summary>
/// The unsubscribe link's own query parameters, round-tripped by the <c>/unsubscribe</c> frontend
/// page. <see cref="Token"/> is <see cref="UnsubscribeTokenService.GenerateToken"/>'s output.
/// </summary>
public sealed record UnsubscribeRequest(
    Guid UserId, [property: StringEnum(typeof(NotificationCategory))] string Category, string Token);
