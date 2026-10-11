namespace KiriChess.Server.Auth;

/// <summary>
/// Phase 8a.2 added the two consent flags. They are REQUIRED and have no default: a consent field
/// that defaults to accepted is not a consent field. Both are self-declared - see README scope
/// decision 6 for why no date of birth is collected. DH.3 added <c>TurnstileToken</c>: optional and
/// defaulted, since it is only checked when <c>Turnstile:SecretKey</c> is configured (see
/// <c>TurnstileVerifier</c>), and a bare checkout or existing test must keep registering without one.
/// </summary>
public sealed record RegisterRequest(
    string Email, string Password, string DisplayName,
    bool AcceptedTerms, bool ConfirmedMinimumAge, string? TurnstileToken = null);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record ConfirmEmailRequest(Guid UserId, string Token);
public sealed record ResendConfirmationRequest(string Email);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
public sealed record ExternalExchangeRequest(string Code);

public sealed record AuthResponse(
    Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
    DateTimeOffset ExpiresAt, string[] Roles);

/// <summary>
/// Token pair from <c>POST /api/auth/external/exchange</c>. Same fields as <see cref="AuthResponse"/>
/// plus <see cref="Created"/>, which is true only when this handshake registered the account (M-4-1).
/// A returning Google sign-in and a link onto an existing password account are false, so the client
/// signup counter does not count them. Login, register, and refresh keep returning
/// <see cref="AuthResponse"/> and never carry the flag.
/// </summary>
public sealed record ExternalExchangeResponse(
    Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
    DateTimeOffset ExpiresAt, string[] Roles, bool Created)
{
    public static ExternalExchangeResponse From(AuthResponse auth, bool created) =>
        new(auth.UserId, auth.Email, auth.DisplayName, auth.Token, auth.RefreshToken,
            auth.ExpiresAt, auth.Roles, created);
}

// Phase 9d.7: these three exist only so /confirm-email, /resend-confirmation and /forgot-password /
// /reset-password have a named schema for `.Produces<T>()` to point at. The handlers keep returning
// their anonymous objects (`new { confirmed = true }` etc.) unchanged - see the phase README, scope
// decision 1. Minimal APIs serialize anonymous objects and records identically under
// System.Text.Json.JsonSerializerDefaults.Web, so this is metadata-only: it names a shape that was
// already on the wire, and does not touch what any endpoint actually sends.
public sealed record ConfirmEmailResponse(bool Confirmed);
public sealed record SentResponse(bool Sent);
public sealed record ResetPasswordResponse(bool Reset);

/// <summary>
/// Phase 9e.6. One active refresh-token session: a device or browser that is currently signed in.
/// No device name or IP is tracked (6b.1 never captured either), so a session is identified by when
/// it was created and when it expires - <c>IsCurrent</c> is the one thing that lets the UI tell the
/// caller's own session apart from the others without asking them to guess by timestamp.
/// </summary>
public sealed record SessionDto(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
