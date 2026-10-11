using System.Security.Claims;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Email;
using KiriChess.Server.Moderation;
using KiriChess.Server.Moderation.Content;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>Minimal-API endpoints for the full auth lifecycle: register, login, refresh, logout, email confirmation, password reset.</summary>
public static class AuthEndpoints
{
    private const string LockedOutMessage = "Account locked due to repeated failed attempts. Try again later.";
    private const string InvalidCredentialsMessage = "Invalid email or password.";
    private const string InvalidSecondFactorMessage = "Invalid or expired code. Try signing in again.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Phase 6e.1: covers register, login, refresh, logout, logout-all, confirm-email,
        // resend-confirmation, forgot-password and reset-password in one line, partitioned by IP,
        // since the caller here is by definition not yet authenticated.
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/register", async (
            RegisterRequest req, UserManager<ApplicationUser> users, TokenService tokens,
            RefreshTokenService refreshTokens, IEmailSender email, EmailOptions emailOptions,
            DisplayNameService displayNames, BreachedPasswordChecker breachedPasswords,
            TurnstileVerifier turnstile, KillSwitchService killSwitches, BanEvasionMarker banEvasion,
            IdentityErrorDescriber identityErrors, CancellationToken ct) =>

        {
            // Phase 10c.4. Ahead of even the consent check below: a signup flood is the threat this
            // switch maps to, and it must not be possible to use the register endpoint as a free
            // oracle for anything - including "is registration currently on" - while it is off.
            if (!await killSwitches.IsEnabledAsync(KillSwitch.Registration, ct))
                return Results.Json(new { error = "Registration is temporarily disabled. Please try again shortly." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // Phase 8a.2. Checked before anything else in the handler: it costs no round trip, where
            // the display-name check below costs one - and it must not be possible to use the register
            // endpoint as a free display-name availability oracle by posting without consenting.
            //
            // Reported per-field rather than as one message so the form can mark the exact checkbox,
            // matching how every other validation failure on this endpoint is shaped.
            var consentErrors = new Dictionary<string, string[]>();
            if (!req.AcceptedTerms)
                consentErrors["AcceptedTerms"] = ["You must accept the Terms of Service and Privacy Policy."];
            if (!req.ConfirmedMinimumAge)
                consentErrors["ConfirmedMinimumAge"] = ["You must confirm that you are at least 16 years old."];
            if (consentErrors.Count > 0)
                return Results.ValidationProblem(consentErrors);

            // DH.3. The register form only forwards the unused widget token; this is the only
            // siteverify. A script that skips the browser never meets the widget.
            // This is the actual enforcement, checked before the display-name round trip for the same
            // "don't give a bot a free oracle" reason as the consent check above. No-ops entirely
            // when Turnstile:SecretKey is unset (see TurnstileVerifier), so local dev and every
            // existing register test are unaffected.
            if (!await turnstile.IsValidAsync(req.TurnstileToken, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["TurnstileToken"] = ["Verification failed. Please try again."]
                });

            // Phase 8a.4: the old fallback (`DisplayName = ... ? req.Email : req.DisplayName`) published
            // the account's email address as its public handle whenever the field was blank. There is no
            // replacement fallback - a blank or otherwise-disallowed name is now a validation failure,
            // decided by ContentPolicy rule 1 (TooShort) below.
            var verdict = await displayNames.CheckAsync(req.DisplayName, excludeUserId: null, ct);
            if (!verdict.Allowed)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["DisplayName"] = [verdict.Message]
                });

            // Phase 9d.1. 6b.2's lockout limits online guessing; it does nothing about a password
            // that is already public. Checked after the cheap local validation above but before
            // CreateAsync, same "cheapest checks first" ordering as the rest of this handler.
            if (await breachedPasswords.IsBreachedAsync(req.Password, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Password"] = ["This password has appeared in a data breach. Choose a different one."]
                });

            // S-3-1. The last check before CreateAsync, and that position is the whole enumeration
            // argument rather than an accident of where it was easiest to paste.
            //
            // TERMS section 6 promises that deleting an account and registering again cannot launder
            // a ban. Erasure releases the email address and the display name back into the pool, so
            // the only thing that can keep that promise is refusing the address, and refusing it has
            // to be indistinguishable from the answer a *live* account's address already gets. Two
            // things make it so. The shape: IdentityErrorDescriber produces the identical code and
            // description string CreateAsync would have returned for a duplicate email, so the body
            // is byte-for-byte the taken-address response and not a second, recognisable one. The
            // position: every cheaper check (consent, Turnstile, display name, breached password)
            // has already run and can already have answered, so a probe cannot tell "banned" from
            // "taken" by which checks it got past first. Moving this earlier - above the display-name
            // round trip, say - would reintroduce exactly that distinguisher and turn the endpoint
            // into the ban oracle this check exists to avoid.
            //
            // S-5-5 is the open finding about /forgot-password leaking address existence. This does
            // not fix that and does not contradict it: register already answers "taken" for a live
            // address, and the set of addresses that answer that way simply grows by the banned
            // tombstones, which is strictly less information than a separate refusal would give.
            //
            // Inert unless BanEvasion:MarkerSecret is set - see BanEvasionMarker.
            if (await banEvasion.IsRegistrationRefusedAsync(users.NormalizeEmail(req.Email), ct))
            {
                var duplicate = identityErrors.DuplicateEmail(req.Email);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [duplicate.Code] = [duplicate.Description]
                });
            }

            var user = new ApplicationUser
            {
                UserName = req.Email,
                Email = req.Email,
                LockoutEnabled = true,
                TermsAcceptedAt = DateTimeOffset.UtcNow,
                TermsVersion = TermsVersion.Current,
                AgeConfirmedAt = DateTimeOffset.UtcNow,
            };
            DisplayNameService.Apply(user, req.DisplayName.Trim(), verdict.Normalized, changedAt: null);

            IdentityResult result;
            try
            {
                result = await users.CreateAsync(user, req.Password);
            }
            catch (DbUpdateException)
            {
                // CheckAsync above and CreateAsync below are two round trips, so two simultaneous
                // registrations of the same name can both pass the check - the unique index on
                // NormalizedDisplayName is the only thing that can arbitrate that race, and it is the
                // authority here. DisplayName is the only unique constraint a register can newly violate
                // that Identity does not already check itself: Identity checks NormalizedUserName and
                // NormalizedEmail internally and returns a clean IdentityResult for both, so neither of
                // those ever reaches this catch. Report it as the 400 it really is instead of a 500.
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["DisplayName"] = ["That name was just taken. Try another."]
                });
            }
            if (!result.Succeeded)
            {
                // UserName is set to the email above. Identity then reports DuplicateUserName *and*
                // DuplicateEmail for the same collision, so the form used to say the email was a
                // taken username. DuplicateEmail is the honest one.
                var errors = result.Errors
                    .Where(e => e.Code != "DuplicateUserName")
                    .ToDictionary(e => e.Code, e => new[] { e.Description });
                if (errors.Count == 0)
                {
                    errors["DuplicateEmail"] = ["Email is already taken."];
                }
                return Results.ValidationProblem(errors);
            }

            var confirmToken = await users.GenerateEmailConfirmationTokenAsync(user);
            await email.SendAsync(EmailTemplates.Confirmation(emailOptions, user.Email!, user.Id, confirmToken), ct);

            return Results.Ok(await IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct));
        })
        // 9d.7: ValidationProblem covers every named validation dictionary in this handler (consent,
        // display name, breached password, Identity's own CreateAsync errors) - one metadata entry for
        // all of them, matching how ASP.NET itself groups every 422-shaped ValidationProblem response.
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem()
        // The Registration kill switch answers 503 before any of the validation dictionaries above.
        // Undeclared, the generated client has no knowledge of a response the handler actually returns.
        .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/login", async (
            LoginRequest req, UserManager<ApplicationUser> users, TokenService tokens,
            RefreshTokenService refreshTokens, SanctionPolicy sanctions, IConfiguration config,
            TwoFactorChallengeService challenges, IOptions<TwoFactorOptions> twoFactorOptions, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync((req.Email ?? "").Trim());
            if (user is null) return InvalidCredentials();

            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = LockedOutMessage }, statusCode: StatusCodes.Status423Locked);

            if (!await users.CheckPasswordAsync(user, req.Password))
            {
                // Don't re-check IsLockedOutAsync here: AccessFailedAsync trips the lockout exactly
                // on the attempt that reaches the threshold, so re-checking would report 423 on the
                // *triggering* failed attempt itself. The contract is 423 on the *next* attempt after
                // lockout, the top-of-handler check above is what enforces that.
                await users.AccessFailedAsync(user);
                return InvalidCredentials();
            }

            // Read dynamically at request time (not at builder time), see the README's "never read
            // config eagerly" rule. `config` is injected fresh per request from the built app.
            if (config.GetValue("Auth:RequireConfirmedEmail", false) && !await users.IsEmailConfirmedAsync(user))
                return Results.Json(new { error = "Email not confirmed." }, statusCode: StatusCodes.Status403Forbidden);

            // Phase 8.5: an AccountBan blocks sign-in outright. 403 (not the 401 an unknown user/bad
            // password gets, and not the 423 the lockout path uses above) because this is a distinct
            // fact: the credentials are correct, the account is simply not allowed to sign in.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = BanMessage(ban) }, statusCode: StatusCodes.Status403Forbidden);

            await users.ResetAccessFailedCountAsync(user);

            // Phase 9d.2. The second factor gates token ISSUANCE - 6b.1's rotation and reuse detection
            // are untouched, and /refresh deliberately does not re-prompt (see
            // Phase 9d.2 § 1.1). The password was correct, so the failed-attempt
            // counter is reset above regardless of what happens at the second step; the 2FA step keeps
            // its own failures on that same counter.
            if (user.TwoFactorEnabled)
            {
                var ticket = await challenges.IssueAsync(user.Id, ct);
                return Results.Ok(new TwoFactorRequiredResponse(
                    true, ticket, twoFactorOptions.Value.ChallengeLifetimeSeconds));
            }

            return Results.Ok(await IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct));
        })
        // This route has TWO different 200 shapes (Phase 9d.2): a token pair for an account without
        // 2FA, and a TwoFactorRequiredResponse ticket for one with it. Both are declared; the document
        // then carries the second as the 200 schema, which is a known and accepted limitation of
        // .Produces<T> - it has no oneOf. The union is not lost, because ui/lib/types.ts models the
        // pair as `LoginResult` and narrows with `isTwoFactorChallenge`, and BOTH members are
        // generated schemas in their own right (AuthResponse via /refresh, TwoFactorRequiredResponse
        // here), so Task B's hand-written union is a union OF generated types rather than a
        // hand-mirrored shape. Expressing the oneOf properly needs an operation transformer, which is
        // 9f.5's business along with the rest of the document's public presentation.
        //
        // Unauthorized (401): unknown email or wrong password. Same {error} envelope as the 423/403
        // paths so the client never has to show a raw status code. Locked (423) and the two Forbidden
        // (403) paths - unconfirmed email, an AccountBan - are the other real outcomes; see
        // LockedOutMessage/BanMessage above for the {error} shape both carry.
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .Produces<TwoFactorRequiredResponse>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status423Locked)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPost("/login/2fa", async (
            TwoFactorLoginRequest req, UserManager<ApplicationUser> users, TokenService tokens,
            RefreshTokenService refreshTokens, TwoFactorChallengeService challenges,
            SanctionPolicy sanctions, ILogger<Program> logger, CancellationToken ct) =>
        {
            var userId = await challenges.ResolveAsync(req.Ticket, ct);
            if (userId is null) return InvalidSecondFactor();

            var user = await users.FindByIdAsync(userId.Value.ToString());
            if (user is null) return InvalidSecondFactor();

            // Same order as /login: lockout first, then the ban. A wrong code below calls
            // AccessFailedAsync, so 6b.2's counter bounds guessing at the second step exactly as it
            // does at the first - which is what makes a six-digit code adequate here.
            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = LockedOutMessage }, statusCode: StatusCodes.Status423Locked);

            // Re-checked rather than trusted from /login: the two calls are minutes apart, and this is
            // the point at which the token pair is actually minted.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = BanMessage(ban) }, statusCode: StatusCodes.Status403Forbidden);

            var (verified, usedRecoveryCode) = await VerifySecondFactorAsync(users, user, req.Code);
            if (!verified)
            {
                await users.AccessFailedAsync(user);
                // The ticket is deliberately NOT consumed: a typo must not cost a full re-login.
                return InvalidSecondFactor();
            }

            await challenges.ConsumeAsync(req.Ticket, ct);
            await users.ResetAccessFailedCountAsync(user);

            if (usedRecoveryCode)
            {
                // Worth a line on its own: a recovery code being spent is either "the user lost their
                // phone" or "someone else has their codes", and both are things you want to find in
                // the log later. The user id and the remaining count only - never the code.
                logger.LogWarning(
                    "User {UserId} signed in with a two-factor recovery code; {Remaining} codes remain.",
                    user.Id, await users.CountRecoveryCodesAsync(user));
            }

            return Results.Ok(await IssueAuthResponseAsync(user, users, tokens, refreshTokens, ct));
        })
        // Unauthorized (401): an unknown, expired or already-spent ticket, or a wrong code. Same
        // {error} envelope as /login so the client never surfaces a raw status. Locked (423) is real
        // here too - a wrong code counts against the same lockout counter the password step uses
        // (see the comment on the challenge issue above) - and Forbidden (403) is an AccountBan
        // caught on the way through. Unlike /login this route has exactly one 200 shape.
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status423Locked)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPost("/refresh", async (
            RefreshRequest req, UserManager<ApplicationUser> users, TokenService tokens,
            RefreshTokenService refreshTokens, SanctionPolicy sanctions, CancellationToken ct) =>
        {
            var rotated = await refreshTokens.RotateAsync(req.RefreshToken, ct);
            if (rotated is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(rotated.Value.UserId.ToString());
            if (user is null) return Results.Unauthorized();

            // This is the enforcement point that actually closes the ban hole (Phase 8.5): an access
            // token already in a banned player's hands stays valid until it expires (up to
            // Jwt:ExpiryMinutes), and refresh is where the session finally dies.
            var ban = await sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = BanMessage(ban) }, statusCode: StatusCodes.Status403Forbidden);

            var roles = await users.GetRolesAsync(user);
            var access = tokens.CreateToken(user, roles);
            return Results.Ok(new AuthResponse(
                user.Id, user.Email!, user.DisplayName, access.Value, rotated.Value.RefreshToken,
                access.ExpiresAt, roles.ToArray()));
        })
        // 401: an unknown/expired/already-rotated refresh token, or a user id it names that no longer
        // exists. 403: the ban-enforcement path documented on the handler above.
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPost("/logout", async (LogoutRequest req, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            await refreshTokens.RevokeAsync(req.RefreshToken, ct);
            return Results.NoContent();
        })
        // No body on purpose - revoking an unknown/already-revoked token is still a 204, matching the
        // no-oracle shape the rest of this handler already has (it never says WHICH refresh token was
        // invalid).
        .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/logout-all", async (ClaimsPrincipal principal, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            await refreshTokens.RevokeAllForUserAsync(userId.Value, ct);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/confirm-email", async (ConfirmEmailRequest req, UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByIdAsync(req.UserId.ToString());
            if (user is null) return Results.BadRequest(new { error = "Invalid confirmation request." });

            var result = await users.ConfirmEmailAsync(user, req.Token);
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.ToDictionary(e => e.Code, e => new[] { e.Description }));

            return Results.Ok(new { confirmed = true });
        })
        // The handler still returns the anonymous `{ confirmed = true }` object - ConfirmEmailResponse
        // (AuthDtos.cs) exists only to give this schema a name. See the phase README, scope decision 1.
        .Produces<ConfirmEmailResponse>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .ProducesValidationProblem();

        group.MapPost("/resend-confirmation", async (
            ResendConfirmationRequest req, UserManager<ApplicationUser> users, IEmailSender email,
            EmailOptions emailOptions, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is not null && !await users.IsEmailConfirmedAsync(user))
            {
                var token = await users.GenerateEmailConfirmationTokenAsync(user);
                await email.SendAsync(EmailTemplates.Confirmation(emailOptions, user.Email!, user.Id, token), ct);
            }
            return Results.Ok(new { sent = true });
        })
        // Always 200, deliberately: an unknown email and an already-confirmed email both answer
        // { sent = true } - see the handler's own comment shape and ui/lib/api.ts's callers for why
        // this must never become a confirmation-status oracle. SentResponse names the shared schema.
        .Produces<SentResponse>(StatusCodes.Status200OK);

        group.MapPost("/forgot-password", async (
            ForgotPasswordRequest req, UserManager<ApplicationUser> users, IEmailSender email,
            EmailOptions emailOptions, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is not null)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                await email.SendAsync(EmailTemplates.PasswordReset(emailOptions, user.Email!, token), ct);
            }
            return Results.Ok(new { sent = true });
        })
        // Same always-200 shape as /resend-confirmation, and for the same reason: an unknown email
        // must not be distinguishable from a known one.
        .Produces<SentResponse>(StatusCodes.Status200OK);

        group.MapPost("/reset-password", async (
            ResetPasswordRequest req, UserManager<ApplicationUser> users, RefreshTokenService refreshTokens,
            ApiTokenService apiTokens, BreachedPasswordChecker breachedPasswords, CancellationToken ct) =>
        {
            // S-1-13. An unknown email and a known email with a bad token used to answer with two
            // distinguishable shapes ("Email" versus Identity's own "InvalidToken"), and a known email
            // with a breached new password answered a third way before the token was ever checked -
            // three distinguishable answers made this an account-existence oracle. Every early return
            // below reuses this exact shape so the three collapse into one.
            static IResult InvalidEmailOrToken() => Results.ValidationProblem(
                new Dictionary<string, string[]> { ["Email"] = ["Invalid email or token."] });

            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
                return InvalidEmailOrToken();

            // Verify the token before anything else that could distinguish this response from the
            // unknown-email case above - in particular, before the breached-password check just below,
            // which used to run first and could answer "NewPassword" for a caller with no valid token
            // for this address at all. VerifyUserTokenAsync only checks the token; it does not consume
            // it, so ResetPasswordAsync below verifies the same token again rather than trusting a
            // stale result.
            var tokenValid = await users.VerifyUserTokenAsync(
                user, users.Options.Tokens.PasswordResetTokenProvider,
                UserManager<ApplicationUser>.ResetPasswordTokenPurpose, req.Token);
            if (!tokenValid)
                return InvalidEmailOrToken();

            // Phase 9d.1, same check as /register. Reached only once the token above has proven the
            // caller holds a real reset link for this address, so this can no longer answer for an
            // address the caller has no token for.
            if (await breachedPasswords.IsBreachedAsync(req.NewPassword, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["NewPassword"] = ["This password has appeared in a data breach. Choose a different one."]
                });

            var result = await users.ResetPasswordAsync(user, req.Token, req.NewPassword);
            if (!result.Succeeded)
            {
                // The token was just verified valid above; a fresh InvalidToken here means it was
                // consumed or invalidated in the moment between (e.g. a concurrent reset), not that
                // this caller never had one. Flatten it into the same shape rather than exposing
                // Identity's own error code, for the same reason as the two checks above.
                if (result.Errors.Any(e => e.Code == "InvalidToken"))
                    return InvalidEmailOrToken();
                return Results.ValidationProblem(result.Errors.ToDictionary(e => e.Code, e => new[] { e.Description }));
            }

            // S-1-1. Both token kinds, together, the same way SanctionService revokes them for an
            // AccountBan. A reset is the one action a compromised user is actually told to take, and
            // revoking only refresh tokens left the longer-lived credential alive: AUTH.md § 2 already
            // accepts that a script holding a stolen access token can mint a `kirichess_pat_` PAT that outlives
            // the 15-minute JWT and the victim's own logout, so if the reset does not revoke it, the
            // attacker keeps the account after the victim has done everything they were asked to.
            //
            // This is deliberately NOT the same decision as /logout-all, which must leave API tokens
            // alone (independence from the browser session is the point of a PAT). Signing out is
            // routine; a reset is the recovery action. AUTH.md § 2 now records both sides.
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            await apiTokens.RevokeAllForUserAsync(user.Id, ct);
            return Results.Ok(new { reset = true });
        })
        // ResetPasswordResponse names the still-anonymous `{ reset = true }` return. Every failure path
        // here is a ValidationProblem, including the "unknown email" case above - same no-oracle shape
        // /forgot-password's success path relies on.
        .Produces<ResetPasswordResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        return app;
    }

    /// <summary>
    /// Mints the access/refresh token pair for a user. Internal (not private) so
    /// <see cref="ExternalAuthEndpoints"/> can reuse it after a successful OAuth exchange rather than
    /// duplicating token issuance. Takes <paramref name="users"/> to look up roles (Phase 8.5) so the
    /// caller never has to remember to do it separately.
    /// </summary>
    internal static async Task<AuthResponse> IssueAuthResponseAsync(
        ApplicationUser user, UserManager<ApplicationUser> users, TokenService tokens,
        RefreshTokenService refreshTokens, CancellationToken ct)
    {
        var roles = await users.GetRolesAsync(user);
        var access = tokens.CreateToken(user, roles);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);
        return new AuthResponse(
            user.Id, user.Email!, user.DisplayName, access.Value, refresh, access.ExpiresAt, roles.ToArray());
    }

    /// <summary>
    /// Verifies a second factor: a TOTP code first, then - only if that fails - a single-use recovery
    /// code. Returns whether it verified and whether a recovery code was the thing that verified.
    ///
    /// The TOTP attempt strips spaces and hyphens because authenticator apps display "123 456" and
    /// users paste what they see; the recovery attempt gets the input trimmed but otherwise verbatim,
    /// because Identity compares recovery codes ordinally against what it generated and normalising
    /// them would break a legitimate code. Order matters only for cost, not correctness: a recovery
    /// code fails the TOTP path harmlessly (AuthenticatorTokenProvider int.TryParse's the token first).
    /// </summary>
    internal static async Task<(bool Verified, bool UsedRecoveryCode)> VerifySecondFactorAsync(
        UserManager<ApplicationUser> users, ApplicationUser user, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, false);

        var totp = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, totp))
            return (true, false);

        var redeemed = await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());
        return (redeemed.Succeeded, redeemed.Succeeded);
    }

    private static IResult InvalidCredentials() =>
        Results.Json(new { error = InvalidCredentialsMessage }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidSecondFactor() =>
        Results.Json(new { error = InvalidSecondFactorMessage }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The message shown to a banned player at login (Phase 8.5). The reason is shown; the moderator
    /// note never is - see the SanctionDto contract.
    ///
    /// S-3-3: this used to be a dead end. A banned player cannot sign in, so
    /// <c>ui/components/SanctionBanner.tsx</c> is unreachable to them, and this message was the only
    /// thing they would ever see - with nowhere it told them to go, even though
    /// <c>contact@kirichess.com</c> is already published in the footer. There is deliberately no
    /// appeal process (see <see cref="Data.Entities.UserSanction"/>'s doc comment: the row history is
    /// an audit record, not one), but "no process" is not the same as "no address to write to".
    ///
    /// Internal, not private (C-1-1): every token-minting path enforces the same ban and must say the
    /// same thing when it refuses. <see cref="ExternalAuthEndpoints"/> and
    /// <see cref="TwoFactorEndpoints"/> call this rather than composing a second, subtly different
    /// wording, the same reason <see cref="IssueAuthResponseAsync"/> is shared rather than duplicated.
    /// </summary>
    internal static string BanMessage(UserSanction ban)
    {
        var expiry = ban.ExpiresAt is { } expiresAt
            ? $" This expires on {expiresAt:yyyy-MM-dd}."
            : " This suspension does not expire.";
        return $"Your account is suspended: {ban.Reason}.{expiry} If you believe this is a mistake, write to contact@kirichess.com.";
    }

}
