namespace KiriChess.Server.Auth;

/// <summary>Binds to the <c>TwoFactor</c> configuration section (Phase 9d.2).</summary>
public sealed class TwoFactorOptions
{
    public const string SectionName = "TwoFactor";

    /// <summary>
    /// How long the login challenge ticket lives. Long enough to open an authenticator app and read a
    /// code that has not yet rolled over, short enough that an intercepted ticket is worthless -
    /// deliberately far shorter than ExternalLoginCode's 60 seconds is long, because unlike that code
    /// this one waits on a human.
    /// </summary>
    public int ChallengeLifetimeSeconds { get; set; } = 300;

    /// <summary>How many single-use recovery codes are minted on enable and on regeneration.</summary>
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>The issuer label an authenticator app shows beside the account.</summary>
    public string Issuer { get; set; } = "KiriChess";
}

/// <summary>
/// Phase 9d.2. The claim that carries "this account has 2FA enabled" into the access token, so
/// <see cref="KiriChess.Server.Moderation.ModerationPolicies.Admin"/> can require it.
///
/// The name is "mfa" and NOT the JWT-standard "amr", on purpose:
/// JwtSecurityTokenHandler's default INBOUND claim map rewrites "amr" to a long
/// http://schemas.microsoft.com/claims/... URI, so RequireClaim("amr", "mfa") would silently never
/// match a token that plainly contains "amr":"mfa". See the same hazard documented on
/// <see cref="TokenService.CreateToken"/> for the role claim. "mfa" is in no inbound map and
/// round-trips unchanged, exactly like the existing custom "displayName" claim. A test pins this -
/// do not "standardise" it without running that test.
/// </summary>
public static class TwoFactorClaims
{
    public const string ClaimType = "mfa";
    public const string Enabled = "true";
}
