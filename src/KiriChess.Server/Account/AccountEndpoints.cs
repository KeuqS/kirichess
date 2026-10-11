using System.Security.Claims;
using System.Text.Json;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

/// <summary>
/// Minimal-API endpoints for the Phase 8a.2 GDPR surface: authenticated data export and account
/// erasure. Sits beside <c>Moderation/Content/ContentEndpoints.cs</c> (which already owns
/// <c>/api/account/display-name</c>) without overlapping it.
/// </summary>
public static class AccountEndpoints
{
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account")
                         .RequireAuthorization();

        account.MapGet("/export", async (
            ClaimsPrincipal caller, AccountExportBuilder exportBuilder, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var export = await exportBuilder.BuildAsync(me.Value, ct);
            if (export is null) return Results.NotFound();

            // Results.File is what sets Content-Disposition: attachment - do not hand-write the
            // header. A human is going to open this file, hence WriteIndented.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportJsonOptions);
            return Results.File(bytes, "application/json",
                $"kirichess-export-{DateTimeOffset.UtcNow:yyyyMMdd}.json");
        })
        .RequireRateLimiting(RateLimitPolicies.AccountData)
        // 9d.7: Results.File sets Content-Disposition: attachment, but the response BODY is still the
        // serialized AccountExportDto - describing it as that is the honest, useful answer for the
        // client generator, even though this route also carries a filename header no schema captures.
        .Produces<AccountExportDto>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        account.MapPost("/delete", async (
            DeleteAccountRequest req, ClaimsPrincipal caller, AccountDeletionService deletion,
            UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            // C-1-1 asked whether this route should be ban-gated the way the token-minting paths are.
            // Owner 2026-09-08: keep self-delete. It deliberately is NOT, and this comment is here so
            // the next reader does not re-file it:
            //
            // Erasure is a GDPR Art. 17 right, and refusing it because the person is banned would deny
            // that right outright. PRIVACY.md § 6 already delivers the anti-evasion property by a
            // different route, retaining the UserSanctions row under Art. 17(3)(e) "so that a ban
            // cannot be erased by deleting the account it was issued against and registering again",
            // and AccountDeletionService honours that: the sanction FK is Restrict and the row stays.
            // § 6 also states exactly one precondition on deletion, a game in progress; adding a
            // second would put the code out of step with the published policy.
            //
            // What deletion frees is the display name, and it no longer frees the email address for a
            // banned account: S-3-1 is now built, so AccountDeletionService writes a keyed HMAC of the
            // banned account's NormalizedEmail onto the tombstone before scrubbing it, and
            // /api/auth/register (and the Google create path) refuse an address that hashes to a
            // tombstone still carrying an active ban. The deletion itself is untouched, which is the
            // whole point: the fix is about what survives erasure, never about refusing it.
            // L-2-3 (a banned account's working route to access, portability and erasure) is the
            // admin-only GET/POST /api/moderation/users/{id}/export|erase, not a hard block here.
            //
            // A deliberate speed bump on an irreversible, un-undoable action - the server's own check,
            // not something only the UI enforces.
            if (req.Acknowledgement != "DELETE MY ACCOUNT")
                return Results.BadRequest(new { error = "Type \"DELETE MY ACCOUNT\" exactly to confirm." });

            // S-1-2. Password accounts re-enter the password after the acknowledgement. Google-only
            // skip is mandatory: otherwise Art. 17 erasure is unreachable for those accounts.
            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is not null && Reauthentication.HasPassword(user))
            {
                var passwordError = await Reauthentication.VerifyPasswordAsync(users, user, req.Password);
                if (passwordError is not null)
                    return Results.BadRequest(new { error = passwordError });
            }

            var result = await deletion.DeleteAsync(me.Value, ct);
            return result.Outcome switch
            {
                // AlreadyDeleted reports 404, same as NotFound: the account is gone as far as any
                // caller is concerned, and confirming "already deleted" tells a stolen token something
                // it does not need to know.
                AccountDeletionOutcome.NotFound or AccountDeletionOutcome.AlreadyDeleted => Results.NotFound(),
                AccountDeletionOutcome.GameInProgress => Results.Conflict(new
                {
                    error = "Finish or resign your current game before deleting your account."
                }),
                AccountDeletionOutcome.Deleted => Results.Ok(new DeleteAccountResponse(result.DeletedAt!.Value)),
                _ => throw new InvalidOperationException($"Unhandled {nameof(AccountDeletionOutcome)} '{result.Outcome}'.")
            };
        })
        .RequireRateLimiting(RateLimitPolicies.AccountData)
        .Produces<DeleteAccountResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status404NotFound);

        // Phase 9e.6: the rows already exist (6b.1's refresh tokens, one per issued session), so this
        // is a read over them plus a per-id revoke that reuses RefreshTokenService rather than
        // duplicating its rotation/expiry rules.
        account.MapGet("/sessions", async (
            ClaimsPrincipal caller, HttpRequest request, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            // Sent as a header, never a query string, so it never lands in a proxy access log the
            // way a query parameter would - the raw refresh token is bearer-equivalent material.
            // Hashed immediately and only ever compared, never persisted or echoed back.
            var currentHash = request.Headers.TryGetValue("X-Refresh-Token", out var raw) && raw.Count > 0
                ? RefreshTokenService.HashToken(raw.ToString())
                : null;

            var sessions = await refreshTokens.ListActiveAsync(me.Value, ct);
            var dtos = sessions
                .Select(s => new SessionDto(s.Id, s.CreatedAt, s.ExpiresAt, currentHash is not null && s.TokenHash == currentHash))
                .ToArray();
            return Results.Ok(dtos);
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces<SessionDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        account.MapDelete("/sessions/{id:guid}", async (
            Guid id, ClaimsPrincipal caller, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            // No-oracle shape, same as /api/auth/logout: 204 whether the id existed, was already
            // revoked, or belonged to someone else - RevokeByIdAsync scopes the lookup to `me`, so
            // this can never revoke another account's session even given a guessed id.
            await refreshTokens.RevokeByIdAsync(me.Value, id, ct);
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        // Phase 9f.5: personal access tokens for the public/bot API. JWT-only (this group is the
        // default scheme), so a bot token cannot mint or list tokens. Signing out does not revoke
        // these; they are independently revocable.
        account.MapGet("/api-tokens", async (
            ClaimsPrincipal caller, ApiTokenService apiTokens, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var tokens = await apiTokens.ListActiveAsync(me.Value, ct);
            var dtos = tokens
                .Select(t => new ApiTokenDto(
                    t.Id, t.Name, t.TokenPrefix, ApiTokenService.ScopeList(t),
                    t.CreatedAt, t.ExpiresAt, t.LastUsedAt))
                .ToArray();
            return Results.Ok(dtos);
        })
        .RequireRateLimiting(RateLimitPolicies.ApiTokenManage)
        .Produces<ApiTokenDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        account.MapPost("/api-tokens", async (
            CreateApiTokenRequest req, ClaimsPrincipal caller, ApiTokenService apiTokens,
            UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(me.Value.ToString());
            if (user is null) return Results.Unauthorized();

            // S-1-2. Password accounts re-enter the password before minting. Google-only skip is
            // the finding's own "Consider": there is no password to ask, and a stolen PAT is now
            // revoked by a password reset (S-1-1).
            if (Reauthentication.HasPassword(user))
            {
                var passwordError = await Reauthentication.VerifyPasswordAsync(users, user, req.Password);
                if (passwordError is not null)
                    return Results.BadRequest(new { error = passwordError });
            }

            try
            {
                var (token, raw) = await apiTokens.IssueAsync(
                    me.Value, req.Name ?? "", req.Scopes ?? [], req.ExpiresInDays, ct);
                return Results.Created($"/api/account/api-tokens/{token.Id}", new CreatedApiTokenDto(
                    token.Id, token.Name, token.TokenPrefix, ApiTokenService.ScopeList(token),
                    token.CreatedAt, token.ExpiresAt, token.LastUsedAt, raw));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .RequireRateLimiting(RateLimitPolicies.ApiTokenManage)
        .Produces<CreatedApiTokenDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        account.MapDelete("/api-tokens/{id:guid}", async (
            Guid id, ClaimsPrincipal caller, ApiTokenService apiTokens, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            await apiTokens.RevokeByIdAsync(me.Value, id, ct);
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.ApiTokenManage)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        // Phase 11b.1: the profile-page half of the mail preference. The one-click, no-login link in
        // the email itself is the anonymous NotificationEndpoints.MapNotificationEndpoints route -
        // this pair is for a signed-in visit to the profile page instead.
        account.MapGet("/notifications", async (
            ClaimsPrincipal caller, NotificationPreferenceService preferences, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var all = await preferences.GetAllAsync(me.Value, ct);
            var dto = new NotificationPreferencesDto(
                Enum.GetValues<NotificationCategory>()
                    .Select(c => new NotificationCategoryPreferenceDto(c.ToString(), all.GetValueOrDefault(c, true)))
                    .ToArray());
            return Results.Ok(dto);
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces<NotificationPreferencesDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        account.MapPut("/notifications", async (
            UpdateNotificationPreferenceRequest req, ClaimsPrincipal caller,
            NotificationPreferenceService preferences, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (!Enum.TryParse<NotificationCategory>(req.Category, ignoreCase: true, out var category))
                return Results.BadRequest(new { error = $"Unknown category '{req.Category}'." });

            await preferences.SetSubscribedAsync(me.Value, category, req.Subscribed, ct);
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // Cropped square JPEG. The allowlist is the parser: anything that is not a 64–512 px
        // square JPEG of at most 256 KiB is a 400, never stored. A URL is never accepted.
        account.MapPut("/avatar", async (
            IFormFile file, ClaimsPrincipal caller, AvatarService avatars, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var result = await avatars.UploadAsync(me.Value, file, ct);
            if (!result.Ok && result.Error is null) return Results.NotFound();
            if (!result.Ok) return Results.BadRequest(new { error = result.Error });
            return Results.Ok(new AvatarResponse(result.AvatarUrl));
        })
        .DisableAntiforgery()
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<AvatarResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        account.MapDelete("/avatar", async (
            ClaimsPrincipal caller, AvatarService avatars, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var result = await avatars.ClearAsync(me.Value, ct);
            if (!result.Ok) return Results.NotFound();
            return Results.Ok(new AvatarResponse(null));
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces<AvatarResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // Read fresh rather than served from a token claim, and PlayBlocked is the policy's own verdict
        // rather than a rule the client re-derives - see PlayEligibilityPolicy.IsBlockedByEmailGateAsync
        // for both reasons. Confirmed and PlayBlocked are independent on purpose: a grandfathered
        // account is unconfirmed and not blocked, and the banner needs to tell those apart.
        account.MapGet("/email", async (
            ClaimsPrincipal caller, KiriChessDbContext db, PlayEligibilityPolicy eligibility,
            CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == me.Value)
                .Select(u => new { u.Email, u.EmailConfirmed })
                .FirstOrDefaultAsync(ct);
            if (row is null) return Results.NotFound();

            return Results.Ok(new EmailStatusDto(
                row.Email,
                row.EmailConfirmed,
                await eligibility.IsBlockedByEmailGateAsync(me.Value, ct)));
        })
        .RequireRateLimiting(RateLimitPolicies.AccountManage)
        .Produces<EmailStatusDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

}
