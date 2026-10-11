namespace KiriChess.Server.Data.Entities;

/// <summary>
/// A one-time code handed to the browser after a successful provider callback, exchanged once for a
/// real token pair.
///
/// Why a code and not the tokens themselves: the callback's only way to reach the frontend is a
/// redirect, and anything in that URL lands in browser history, the <c>Referer</c> header, and every
/// proxy log between here and the user. A refresh token is a 30-day credential, it must never take
/// that trip. The code is single-use and dies in 60 seconds, so intercepting the URL after the
/// legitimate exchange is worthless.
///
/// Stored as a SHA-256 hash for the same reason <see cref="RefreshToken"/> is: a database leak must
/// not hand over usable credentials.
/// </summary>
public sealed class ExternalLoginCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    /// True only when the handshake that issued this code registered the account (M-4-1). A returning
    /// sign-in, an email link, and a Google re-authentication for 2FA enable are false. The exchange
    /// copies it onto the token response so the client signup counter can tell a new account from a
    /// login. The code dies in 60 seconds, so this is not a lasting record of how the account was created.
    /// </summary>
    public bool AccountCreated { get; set; }
}
