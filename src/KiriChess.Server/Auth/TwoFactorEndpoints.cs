using System.Security.Claims;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>
/// Phase 9d.2. Minimal-API endpoints for TOTP enrolment and management: status, enrol (rotate the
/// shared key without enabling anything), enable (verify a code and turn 2FA on), disable, and
/// recovery-code regeneration. See Phase 9d.2 § 6.3 for the wire contract.
/// </summary>
public static class TwoFactorEndpoints
{
    public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account/2fa")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.TwoFactor);

        group.MapGet("", async (
            ClaimsPrincipal caller, UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            var remaining = user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0;
            return Results.Ok(new TwoFactorStatusResponse(
                user.TwoFactorEnabled, remaining, Reauthentication.HasPassword(user)));
        })
        .Produces<TwoFactorStatusResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/enrol", async (
            ClaimsPrincipal caller, UserManager<ApplicationUser> users,
            IOptions<TwoFactorOptions> twoFactorOptions, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            if (user.TwoFactorEnabled)
                return Results.Json(new
                {
                    error = "Two-factor authentication is already enabled. Disable it first to re-enrol."
                }, statusCode: StatusCodes.Status409Conflict);

            // Deliberately does NOT enable 2FA (README § 6.3): a user who never finishes scanning the
            // key must not lock themselves out of their own account. Enabling only happens at /enable,
            // after a code produced by this key has actually verified.
            await users.ResetAuthenticatorKeyAsync(user);
            var key = await users.GetAuthenticatorKeyAsync(user);

            var account = user.Email ?? user.UserName ?? user.Id.ToString();
            var uri = BuildOtpauthUri(twoFactorOptions.Value.Issuer, account, key!);
            return Results.Ok(new TwoFactorEnrolmentResponse(key!, uri));
        })
        // 409 Conflict: 2FA is already enabled, so re-enrolling would rotate the key out from under a
        // working authenticator - disable first.
        .Produces<TwoFactorEnrolmentResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/enable", async (
            TwoFactorEnableRequest req, ClaimsPrincipal caller, UserManager<ApplicationUser> users,
            TokenService tokens, RefreshTokenService refreshTokens, ExternalLoginCodeService loginCodes,
            IOptions<TwoFactorOptions> twoFactorOptions, SanctionPolicy sanctions, ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            // C-1-1, found by TokenMintingPathTests rather than named in the finding. This route mints
            // a full token pair, and it is reachable with nothing but an unexpired access token, so a
            // player banned mid-session could toggle 2FA on and off to draw a fresh pair each time and
            // never present the refresh token that /refresh would have rejected. That defeats the
            // check AuthEndpoints calls "the enforcement point that actually closes the ban hole".
            // See the 403 metadata note in ExternalAuthEndpoints for why no .Produces line is added.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = AuthEndpoints.BanMessage(ban) },
                    statusCode: StatusCodes.Status403Forbidden);

            if (user.TwoFactorEnabled)
                return Results.Json(new { error = "Two-factor authentication is already enabled." },
                    statusCode: StatusCodes.Status409Conflict);

            // S-1-2. A stolen 15-minute access token must not enable 2FA: that revokes every session
            // and returns recovery codes to the attacker. Password accounts re-enter the password.
            // Google-only accounts have no password, and skipping here would leave the irrecoverable
            // lockout open, so they must present a just-issued ExternalLoginCode for this user. A
            // password on this branch is not a substitute (there is none).
            if (Reauthentication.HasPassword(user))
            {
                var passwordError = await Reauthentication.VerifyPasswordAsync(users, user, req.Password);
                if (passwordError is not null)
                    return Results.BadRequest(new { error = passwordError });
            }
            else
            {
                var reauthError = await Reauthentication.ConsumeGoogleReauthAsync(
                    loginCodes, user.Id, req.GoogleReauthCode, ct);
                if (reauthError is not null)
                    return Results.BadRequest(new { error = reauthError });
            }

            var (verified, _) = await AuthEndpoints.VerifySecondFactorAsync(users, user, req.Code);
            if (!verified)
                return Results.Json(new { error = "That code isn't right. Check your authenticator app and try again." },
                    statusCode: StatusCodes.Status400BadRequest);

            // Order matters: enable BEFORE minting the fresh pair, or the new token will not carry the
            // mfa claim. Revoke BEFORE minting, or the pair just issued is revoked along with the rest.
            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, twoFactorOptions.Value.RecoveryCodeCount);
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);

            logger.LogInformation("User {UserId} enabled two-factor authentication.", user.Id);

            var auth = await AuthEndpoints.IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct);
            return Results.Ok(new TwoFactorEnableResponse(codes!.ToArray(), auth));
        })
        // 409 Conflict: already enabled. 400: the submitted code did not verify against the enrolled
        // key. The 200 carries a fresh token pair because enabling revokes every refresh token.
        .Produces<TwoFactorEnableResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/disable", async (
            TwoFactorDisableRequest req, ClaimsPrincipal caller, UserManager<ApplicationUser> users,
            TokenService tokens, RefreshTokenService refreshTokens, TwoFactorChallengeService challenges,
            SanctionPolicy sanctions, ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            // C-1-1, the other half of the /enable check above and for the identical reason: this
            // route also mints a token pair off nothing but an unexpired access token.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = AuthEndpoints.BanMessage(ban) },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!user.TwoFactorEnabled)
                return Results.Json(new { error = "Two-factor authentication is not enabled." },
                    statusCode: StatusCodes.Status409Conflict);

            // S-1-2. Password accounts re-enter the password in addition to TOTP/recovery.
            // Google-only skip is mandatory: without it they could never turn 2FA off.
            if (Reauthentication.HasPassword(user))
            {
                var passwordError = await Reauthentication.VerifyPasswordAsync(users, user, req.Password);
                if (passwordError is not null)
                    return Results.BadRequest(new { error = passwordError });
            }

            var (verified, _) = await AuthEndpoints.VerifySecondFactorAsync(users, user, req.Code);
            if (!verified)
                return Results.Json(new { error = "That code isn't right. Check your authenticator app and try again." },
                    statusCode: StatusCodes.Status400BadRequest);

            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user); // destroys the shared secret
            // README § 5: any leftover recovery codes are inert once TwoFactorEnabled is false, because
            // the only redeemer (/api/auth/login/2fa) requires a challenge ticket, which is only ever
            // issued for an account that has 2FA enabled. Deliberately not chased down further -
            // Identity exposes no "clear recovery codes" API short of its private storage literals.
            await challenges.RevokeAllForUserAsync(user.Id, ct);
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);

            // Warning, not Information: for an admin this silently closes the moderation surface, and
            // it is the one event here worth finding in a log.
            logger.LogWarning("User {UserId} disabled two-factor authentication.", user.Id);

            var auth = await AuthEndpoints.IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct);
            return Results.Ok(new TwoFactorDisableResponse(auth));
        })
        // 409 Conflict: not enabled, so there is nothing to disable. 400: the confirming code or
        // recovery code did not verify. The 200 carries a fresh token pair for the same reason
        // /enable's does - disabling revokes every refresh token so no session keeps a stale mfa claim.
        .Produces<TwoFactorDisableResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/recovery-codes", async (
            TwoFactorCodeRequest req, ClaimsPrincipal caller, UserManager<ApplicationUser> users,
            IOptions<TwoFactorOptions> twoFactorOptions, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            if (!user.TwoFactorEnabled)
                return Results.Json(new { error = "Two-factor authentication is not enabled." },
                    statusCode: StatusCodes.Status409Conflict);

            var (verified, _) = await AuthEndpoints.VerifySecondFactorAsync(users, user, req.Code);
            if (!verified)
                return Results.Json(new { error = "That code isn't right. Check your authenticator app and try again." },
                    statusCode: StatusCodes.Status400BadRequest);

            // No token churn: the factors themselves have not changed, only the recovery list.
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, twoFactorOptions.Value.RecoveryCodeCount);
            return Results.Ok(new TwoFactorRecoveryCodesResponse(codes!.ToArray()));
        })
        // 409 Conflict: not enabled, so there is no recovery list to regenerate. 400: the confirming
        // code did not verify. No fresh token pair here - see the handler's own note on why
        // regenerating recovery codes is not token churn.
        .Produces<TwoFactorRecoveryCodesResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// Builds the otpauth:// URI every authenticator app understands. The label is "Issuer:account" and
    /// the issuer is repeated as a parameter - both halves are required by the de-facto Key URI format,
    /// and apps that read only one of them are common enough that omitting either produces entries
    /// labelled with a bare email address. Digits/period/algorithm are stated explicitly rather than left
    /// to each app's defaults, and their values are the ones AuthenticatorTokenProvider actually verifies
    /// against: SHA1, 6 digits, a 30-second step.
    /// </summary>
    private static string BuildOtpauthUri(string issuer, string account, string sharedKey) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={sharedKey}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

}
