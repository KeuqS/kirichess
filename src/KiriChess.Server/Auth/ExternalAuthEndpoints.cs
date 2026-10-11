using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>
/// Minimal-API endpoints for external (OAuth) sign-in, the provider-facing half of Phase 6b.3.
/// <see cref="ExternalLoginService"/> owns the linking policy; this file is only responsible for the
/// HTTP shape: starting the challenge, receiving the provider's callback, and exchanging the
/// resulting one-time code for a real token pair.
/// </summary>
public static class ExternalAuthEndpoints
{
    public static IEndpointRouteBuilder MapExternalAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Same "/api/auth" prefix and rate-limit policy as AuthEndpoints: an unauthenticated OAuth
        // start/exchange is exactly the same abuse surface as register/login.
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapGet("/external/google/start", (string? returnPath, ExternalAuthOptions options) =>
        {
            // Fail closed: an unconfigured deploy never registers the Google handler at all (see
            // Program.cs), so this 404s instead of issuing a Challenge the framework can't service.
            if (!options.GoogleEnabled) return Results.NotFound();

            var props = new AuthenticationProperties
            {
                RedirectUri = "/api/auth/external/callback?returnPath=" + Uri.EscapeDataString(SafeReturnPath(returnPath))
            };
            return Results.Challenge(props, [GoogleDefaults.AuthenticationScheme]);
        })
        // 9d.7: this is a browser navigation, never fetched - see googleSignInUrl's doc comment in
        // ui/lib/api.ts. Both responses are bodiless: the 302 is the OAuth challenge redirect to
        // Google, and the 404 is the fail-closed "Google isn't configured" path above.
        .Produces(StatusCodes.Status302Found)
        .Produces(StatusCodes.Status404NotFound);

        // Reached only via the redirect the Google handler issues after /signin-google completes
        // (SignInScheme = ExternalScheme in Program.cs), never called directly by our own code, and
        // never reachable by a caller who didn't just complete a real Google handshake, because the
        // external cookie it reads is what AuthenticateAsync below depends on.
        group.MapGet("/external/callback", async (
            string? returnPath, HttpContext http, ExternalAuthOptions options, ExternalLoginService logins,
            ExternalLoginCodeService codes, ILogger<Program> logger, CancellationToken ct) =>
        {
            // Same fail-closed guard as /start, and load-bearing rather than symmetric: when Google
            // is unconfigured the external cookie scheme is never registered either (see Program.cs),
            // so AuthenticateAsync below would throw "No authentication handler is registered for the
            // scheme 'External'". This path is anonymous and publicly reachable, so without the guard
            // an unconfigured deploy, the shipped default, answers a stray GET with a 500 and a
            // Sentry event instead of a 404.
            if (!options.GoogleEnabled) return Results.NotFound();

            var safePath = SafeReturnPath(returnPath);

            var result = await http.AuthenticateAsync(ExternalAuthDefaults.ExternalScheme);
            await http.SignOutAsync(ExternalAuthDefaults.ExternalScheme); // its whole job is done; do not leave it set

            if (!result.Succeeded || result.Principal is null)
            {
                // Not our failure to diagnose further: the user cancelled at Google, or the
                // correlation/state cookie was lost (see the Data Protection note in Program.cs).
                logger.LogInformation("External sign-in ({Provider}) did not complete", GoogleDefaults.AuthenticationScheme);
                return RedirectToFrontend("cancelled");
            }

            var identity = ExternalLoginService.FromPrincipal(GoogleDefaults.AuthenticationScheme, result.Principal);
            if (identity is null)
            {
                logger.LogInformation("External sign-in ({Provider}) sent no usable identity", GoogleDefaults.AuthenticationScheme);
                return RedirectToFrontend("unavailable");
            }

            var loginResult = await logins.SignInOrCreateAsync(identity, ct);

            // Never log the email, the code, or any claim value, only the provider and the outcome.
            logger.LogInformation("External sign-in {Provider} outcome {Outcome}", GoogleDefaults.AuthenticationScheme, loginResult.Outcome);

            if (loginResult.Outcome == ExternalLoginOutcome.UnverifiedEmail)
                return RedirectToFrontend("unverified_email");
            // S-5-2. Falls back to the generic errorDefault copy on the callback page (no
            // ERROR_MESSAGE_KEYS entry for this slug) rather than adding new translation surface for a
            // switch flip that is rare and already explained to the operator, the same "don't add a
            // distinct slug for this" call S-3-1 made for a banned signup two lines below.
            if (loginResult.Outcome == ExternalLoginOutcome.RegistrationPaused)
                return RedirectToFrontend("registration_paused");
            if (loginResult.Outcome == ExternalLoginOutcome.LinkRefused || loginResult.User is null)
                return RedirectToFrontend("link_refused");

            var code = await codes.IssueAsync(
                loginResult.User.Id, loginResult.Outcome == ExternalLoginOutcome.Created, ct);
            var target = $"{options.FrontendBaseUrl}/auth/callback?code={Uri.EscapeDataString(code)}&returnPath={Uri.EscapeDataString(safePath)}";
            return Results.Redirect(target);

            IResult RedirectToFrontend(string errorSlug) =>
                Results.Redirect($"{options.FrontendBaseUrl}/auth/callback?error={errorSlug}");
        })
        // Same "browser navigation, never fetched" shape as /external/google/start: every path through
        // this handler ends in either the fail-closed 404 or a bodiless redirect to the frontend
        // (success or one of the RedirectToFrontend error slugs).
        .Produces(StatusCodes.Status302Found)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/external/exchange", async (
            ExternalExchangeRequest req, ExternalLoginCodeService codes, UserManager<ApplicationUser> users,
            TokenService tokens, RefreshTokenService refreshTokens, TwoFactorChallengeService challenges,
            SanctionPolicy sanctions, IOptions<TwoFactorOptions> twoFactorOptions, CancellationToken ct) =>
        {
            var consumed = await codes.ConsumeAsync(req.Code, ct);
            if (consumed is null)
                return Results.BadRequest(new { error = "This sign-in link has expired or was already used. Please sign in again." });

            var user = await users.FindByIdAsync(consumed.Value.UserId.ToString());
            if (user is null)
                return Results.BadRequest(new { error = "This sign-in link has expired or was already used. Please sign in again." });

            // C-1-1. This is a token-minting path, so it enforces the ban the same way /login,
            // /login/2fa and /refresh do (Phase 8.5). Without it a banned player who can still
            // complete the Google handshake kept drawing fresh 15-minute access tokens indefinitely,
            // because refresh is checked and this was not.
            //
            // Ordered before the 2FA branch for the same reason /login orders it that way: a banned
            // account should not even be issued a challenge ticket, since the only thing that ticket
            // can buy is a token pair this check is about to refuse anyway.
            //
            // 403, matching every other enforcement point: the handshake really did succeed, the
            // account simply is not allowed to sign in. Deliberately NOT declared with a
            // .Produces<ErrorResponse>(403) below, because that would change the committed
            // docs/api/openapi.json and this change is barred from running the OpenAPI export. The
            // metadata line is the one loose end here and is reported as a follow-up.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = AuthEndpoints.BanMessage(ban) },
                    statusCode: StatusCodes.Status403Forbidden);

            // Phase 9d.2. A linked Google account must not be a way around the account's own second
            // factor: without this branch, anyone who can complete the Google handshake for that
            // address bypasses TOTP entirely, which would make 2FA optional in practice for every
            // account that ever linked a provider.
            if (user.TwoFactorEnabled)
            {
                var ticket = await challenges.IssueAsync(user.Id, ct);
                return Results.Ok(new TwoFactorRequiredResponse(
                    true, ticket, twoFactorOptions.Value.ChallengeLifetimeSeconds));
            }

            var auth = await AuthEndpoints.IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct);
            return Results.Ok(ExternalExchangeResponse.From(auth, consumed.Value.AccountCreated));
        })
        // Two 200 shapes, same as /login: a token pair (with Created) when the account has no second
        // factor, and a TwoFactorRequiredResponse when it does. .Produces is one type per status, so
        // the second declaration is the one the document generator registers. ExternalExchangeResponse
        // is named second on purpose: nothing else returns it, and TwoFactorRequiredResponse is already
        // registered by /login. MultiShapeResponseTransformer turns the pair into a oneOf.
        .Produces<TwoFactorRequiredResponse>(StatusCodes.Status200OK)
        .Produces<ExternalExchangeResponse>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }

    /// <summary>
    /// Reduces a caller-supplied post-login destination to a safe site-relative path. Anything that is
    /// not plainly relative collapses to "/". An auth callback that reflects a caller-supplied absolute
    /// URL is an open redirect, and an open redirect on a sign-in endpoint is a phishing primitive: the
    /// victim really did sign in to the real site, then got handed to the attacker's page.
    /// </summary>
    internal static string SafeReturnPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "/";
        if (!candidate.StartsWith('/')) return "/";          // absolute or scheme-relative
        if (candidate.StartsWith("//")) return "/";           // protocol-relative -> another origin
        if (candidate.Contains(':')) return "/";               // scheme smuggling
        if (candidate.Contains('\\')) return "/";              // backslash normalisation tricks
        return candidate;
    }
}
