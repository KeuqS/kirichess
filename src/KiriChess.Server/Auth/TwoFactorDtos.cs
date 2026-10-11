namespace KiriChess.Server.Auth;

/// <summary>
/// Phase 9d.2. The alternative 200 body from /api/auth/login and /api/auth/external/exchange when the
/// account has 2FA enabled. <paramref name="TwoFactorRequired"/> is always true and exists purely as
/// the discriminator the client switches on - <see cref="AuthResponse"/> never carries the field.
/// </summary>
public sealed record TwoFactorRequiredResponse(
    bool TwoFactorRequired, string TwoFactorTicket, int ExpiresInSeconds);

public sealed record TwoFactorLoginRequest(string Ticket, string Code);
public sealed record TwoFactorCodeRequest(string Code);

/// <summary>
/// S-1-2. Enable is split off <see cref="TwoFactorCodeRequest"/> so /recovery-codes does not advertise
/// a password it discards. <paramref name="Password"/> is required when the account has one;
/// <paramref name="GoogleReauthCode"/> is required when it does not (a just-issued
/// <c>ExternalLoginCode</c> for this user). Requiredness is enforced in the handler.
/// </summary>
public sealed record TwoFactorEnableRequest(string Code, string? Password, string? GoogleReauthCode);

/// <summary>
/// S-1-2. Disable keeps the existing TOTP/recovery confirmation and adds <paramref name="Password"/>
/// when the account has one. Google-only accounts skip the password or they could never turn 2FA off.
/// </summary>
public sealed record TwoFactorDisableRequest(string Code, string? Password);

public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesRemaining, bool HasPassword);
public sealed record TwoFactorEnrolmentResponse(string SharedKey, string OtpauthUri);

/// <summary>
/// The recovery codes are shown exactly once, here. Nothing reads them back. <paramref name="Auth"/>
/// is a freshly minted pair: enabling 2FA revokes every existing refresh token for the account, so
/// the caller's own session would otherwise die along with the others.
/// </summary>
public sealed record TwoFactorEnableResponse(string[] RecoveryCodes, AuthResponse Auth);
public sealed record TwoFactorDisableResponse(AuthResponse Auth);
public sealed record TwoFactorRecoveryCodesResponse(string[] RecoveryCodes);
