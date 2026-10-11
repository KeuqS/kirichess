using Microsoft.AspNetCore.Authentication;

namespace KiriChess.Server.Auth;

/// <summary>
/// Authenticates the secondary schemes on the two surfaces that accept API tokens (Phase 9f.5),
/// without making those tokens the app-wide default. Default authenticate/challenge stays JWT, so
/// existing <c>RequireAuthorization()</c> endpoints keep rejecting a <c>kirichess_pat_</c> bearer.
///
/// <list type="bullet">
/// <item><c>/hubs/game</c> uses <see cref="HubAuthDefaults.Scheme"/> (JWT or API token).</item>
/// <item><c>/api/v1</c> uses <see cref="ApiTokenDefaults.Scheme"/> so token-id rate-limit partitions
/// are populated before the limiter runs (authentication middleware only runs the default JWT scheme).</item>
/// </list>
/// Presence stays on the JWT default: a <c>kirichess_pat_</c> token there is simply anonymous.
/// </summary>
public sealed class HubAuthOverlayMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/hubs/game"))
        {
            var result = await context.AuthenticateAsync(HubAuthDefaults.Scheme);
            if (result.Succeeded && result.Principal is not null)
                context.User = result.Principal;
        }
        else if (context.Request.Path.StartsWithSegments("/api/v1"))
        {
            var result = await context.AuthenticateAsync(ApiTokenDefaults.Scheme);
            if (result.Succeeded && result.Principal is not null)
                context.User = result.Principal;
        }

        await next(context);
    }
}
