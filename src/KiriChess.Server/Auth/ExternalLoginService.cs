using System.Security.Claims;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using KiriChess.Server.Moderation.Content;
using Microsoft.AspNetCore.Identity;

namespace KiriChess.Server.Auth;

/// <summary>A provider-asserted identity, normalised out of the provider's claims.</summary>
public sealed record ExternalIdentity(
    string Provider, string ProviderKey, string? Email, bool EmailVerified, string? DisplayName);

/// <summary>The result of attempting to sign in or create an account from an <see cref="ExternalIdentity"/>.</summary>
public enum ExternalLoginOutcome
{
    /// <summary>An existing provider login matched. The normal repeat sign-in.</summary>
    SignedIn,

    /// <summary>No account existed; one was created from the provider identity.</summary>
    Created,

    /// <summary>The provider was linked onto an existing local account.</summary>
    Linked,

    /// <summary>The provider did not assert a verified email. Refused, see below.</summary>
    UnverifiedEmail,

    /// <summary>Identity refused the create or the link (duplicate username, validation).</summary>
    LinkRefused,

    /// <summary>
    /// S-5-2: the <see cref="KillSwitch.Registration"/> switch is off and this identity would
    /// otherwise have created a new account. An existing user signing in (<see cref="SignedIn"/> or
    /// <see cref="Linked"/>) never reaches this outcome - only account creation is registration.
    /// </summary>
    RegistrationPaused
}

/// <summary>Outcome of <see cref="ExternalLoginService.SignInOrCreateAsync"/>. <see cref="User"/> is null only when nothing was signed in.</summary>
public sealed record ExternalLoginResult(ApplicationUser? User, ExternalLoginOutcome Outcome);

/// <summary>
/// The provider-agnostic linking policy for external sign-in. Deliberately a separate, injectable
/// service and not inline endpoint code, precisely so this policy can be unit/integration tested
/// without a browser or a live Google, see <c>ExternalLoginTests</c>.
/// </summary>
public sealed class ExternalLoginService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly DisplayNameService _displayNames;
    private readonly BanEvasionMarker _banEvasion;
    private readonly KillSwitchService _killSwitches;

    public ExternalLoginService(
        UserManager<ApplicationUser> users, DisplayNameService displayNames, BanEvasionMarker banEvasion,
        KillSwitchService killSwitches)
    {
        _users = users;
        _displayNames = displayNames;
        _banEvasion = banEvasion;
        _killSwitches = killSwitches;
    }

    /// <summary>
    /// Implements the six pinned security decisions from the phase 6b.3 plan. The order below is
    /// load-bearing, do not reorder it.
    /// </summary>
    public async Task<ExternalLoginResult> SignInOrCreateAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        // 1) Identity is the provider key, never the email. An email is an attribute of an account,
        // not its name, looking up by email first would let anyone claim an account merely by
        // presenting *a* provider identity asserting the victim's address (see step 2's guard for why
        // that address assertion itself cannot be trusted blindly either).
        var existingUser = await _users.FindByLoginAsync(identity.Provider, identity.ProviderKey);
        if (existingUser is not null)
        {
            // Lockout note (pinned decision 6): never call IsLockedOutAsync on this path. Lockout
            // protects the password; a provider sign-in does not use a password, and honouring
            // lockout here would let anyone lock a victim out of their own working Google login by
            // spraying bad passwords against the password login. Reset the failed count instead, the
            // same way a successful password login would.
            await _users.ResetAccessFailedCountAsync(existingUser);
            return new ExternalLoginResult(existingUser, ExternalLoginOutcome.SignedIn);
        }

        // 2) Never link on an unverified email. Auto-linking on an unverified provider email is the
        // classic account takeover: an attacker registers a provider account bearing the victim's
        // address and inherits the victim's password account. Google always sends email_verified;
        // this code must not assume any provider does, so the check is unconditional.
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
            return new ExternalLoginResult(null, ExternalLoginOutcome.UnverifiedEmail);

        // 3) A verified email may link onto an existing password account. The provider has proven
        // control of the address, that is the same proof /confirm-email would have demanded, so
        // this is not a weaker gate than the email-confirmation flow.
        var byEmail = await _users.FindByEmailAsync(identity.Email);
        if (byEmail is not null)
        {
            var linkResult = await _users.AddLoginAsync(byEmail, new UserLoginInfo(identity.Provider, identity.ProviderKey, identity.Provider));
            if (!linkResult.Succeeded)
                return new ExternalLoginResult(null, ExternalLoginOutcome.LinkRefused);

            if (!byEmail.EmailConfirmed)
            {
                byEmail.EmailConfirmed = true;
                await _users.UpdateAsync(byEmail);
            }

            await _users.ResetAccessFailedCountAsync(byEmail);
            return new ExternalLoginResult(byEmail, ExternalLoginOutcome.Linked);
        }

        // 3a) S-5-2. Same ordering rule /api/auth/register uses for this switch (AuthEndpoints.cs):
        // checked ahead of every other new-account-only gate, including the ban-evasion check right
        // below, so this path cannot be used as an oracle for anything - including "is registration
        // currently on" - while it is off. Placed here rather than at the top of the method because
        // steps 1 and 3 above are sign-in and linking, not registration, and must not be blocked by
        // this switch; only a path that is actually about to create an account reaches this line.
        if (!await _killSwitches.IsEnabledAsync(KillSwitch.Registration, ct))
            return new ExternalLoginResult(null, ExternalLoginOutcome.RegistrationPaused);

        // 3b) S-3-1. Creating an account is creating an account, whoever asked for it. The register
        // form is not the only path that hands a banned player a fresh, unsanctioned account after
        // they erase the banned one - "Sign in with Google" under the same address is the other one,
        // and it reaches step 4 rather than step 3 precisely because erasure nulled the Email column
        // that FindByEmailAsync above looks at. Gating only /api/auth/register would have shipped a
        // control with a one-click bypass and left TERMS section 6 as false as it was.
        //
        // Reported as LinkRefused rather than a new outcome. That is deliberate: the callback's error
        // slugs are player-facing copy in six locales, and a distinct "you are banned" slug would
        // both add translation surface and say more than the register path says. The existing message
        // ("we couldn't connect that Google account to a KiriChess account") is true here. Nobody is
        // enumerated by it either, since reaching this line at all requires having just completed a
        // real Google handshake for that address.
        //
        // Inert unless BanEvasion:MarkerSecret is set - see BanEvasionMarker.
        if (await _banEvasion.IsRegistrationRefusedAsync(_users.NormalizeEmail(identity.Email), ct))
            return new ExternalLoginResult(null, ExternalLoginOutcome.LinkRefused);

        // 4) No account at all, create one. No password: this account signs in via the provider only.
        //
        // Phase 8a.4: the register path (AuthEndpoints) *refuses* a disallowed name and asks the caller
        // to try again. This path cannot do that - an OAuth callback has nobody to ask, the account is
        // being created right now, and refusing would fail a sign-in for a user who did nothing wrong.
        // So DeriveAvailableAsync sanitizes and suffixes instead of rejecting. That asymmetry between the
        // two paths is intentional, not an oversight - do not "fix" it into a rejection here.
        // Phase 8a.2. Stamped at account creation because the OAuth round trip has nowhere to
        // carry a checkbox: the consent is the line of text under the Google button on the login
        // page, not an explicit tick. This is a weaker basis than the register form's and is
        // booked as such in the roadmap entry - it is the same trade every OAuth signup on the web
        // makes, and the alternative is an extra interstitial between Google's redirect and a
        // working session.
        var user = new ApplicationUser
        {
            UserName = identity.Email,
            Email = identity.Email,
            DisplayName = await _displayNames.DeriveAvailableAsync(identity.DisplayName, identity.Email, ct),
            EmailConfirmed = true, // the provider verified it; see step 2's guard
            LockoutEnabled = true,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            TermsVersion = TermsVersion.Current,
            AgeConfirmedAt = DateTimeOffset.UtcNow,
        };
        // NormalizedDisplayName is deliberately not set here (README scope decision 10): the DbContext's
        // SaveChanges override recomputes it for every added/modified ApplicationUser, so every write
        // path stays correct even if one forgets. DisplayNameService.Apply exists for handlers that need
        // the normalized key on the in-memory entity before saving; this path does not.
        var created = await _users.CreateAsync(user);
        if (!created.Succeeded)
            return new ExternalLoginResult(null, ExternalLoginOutcome.LinkRefused);

        var addLogin = await _users.AddLoginAsync(user, new UserLoginInfo(identity.Provider, identity.ProviderKey, identity.Provider));
        if (!addLogin.Succeeded)
        {
            // A user row with no password and no provider login is an unreachable orphan, nothing
            // could ever sign into it. Better to delete it than leave dead rows behind.
            await _users.DeleteAsync(user);
            return new ExternalLoginResult(null, ExternalLoginOutcome.LinkRefused);
        }

        return new ExternalLoginResult(user, ExternalLoginOutcome.Created);
    }

    /// <summary>
    /// Normalises the claims a <see cref="ClaimsPrincipal"/> from a successful provider callback
    /// carries into an <see cref="ExternalIdentity"/>. Returns null when the provider did not send a
    /// stable identifier, without one there is nothing to key an account on.
    /// </summary>
    public static ExternalIdentity? FromPrincipal(string provider, ClaimsPrincipal principal)
    {
        var providerKey = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(providerKey)) return null;

        var email = principal.FindFirstValue(ClaimTypes.Email);
        var displayName = principal.FindFirstValue(ClaimTypes.Name);
        var emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);

        return new ExternalIdentity(provider, providerKey, email, emailVerified, displayName);
    }
}
