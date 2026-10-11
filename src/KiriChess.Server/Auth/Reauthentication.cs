using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;

namespace KiriChess.Server.Auth;

/// <summary>
/// S-1-2. Shared re-authentication for the four irreversible account actions (2FA enable/disable,
/// erasure, PAT mint). Password accounts must present the password; Google-only accounts skip on
/// disable/delete/mint (otherwise those exits close) and must present a freshly consumed
/// <see cref="ExternalLoginCode"/> on enable (otherwise a stolen access token can lock the account).
/// </summary>
internal static class Reauthentication
{
    internal const string PasswordRequiredMessage = "Enter your password to continue.";
    internal const string WrongPasswordMessage = "That password isn't right.";
    internal const string GoogleReauthRequiredMessage = "Confirm with Google to continue.";

    internal static bool HasPassword(ApplicationUser user) =>
        !string.IsNullOrEmpty(user.PasswordHash);

    /// <summary>
    /// Returns null when the password is accepted, otherwise the 400 <c>error</c> string. Empty and
    /// whitespace are a distinct message from a wrong password so the UI can match the empty case
    /// before submit; both are still 400, never 401 (a 401 would make the generated client retry
    /// the same body after a silent refresh).
    /// </summary>
    internal static async Task<string?> VerifyPasswordAsync(
        UserManager<ApplicationUser> users, ApplicationUser user, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return PasswordRequiredMessage;

        return await users.CheckPasswordAsync(user, password)
            ? null
            : WrongPasswordMessage;
    }

    /// <summary>
    /// Consumes a one-time Google handshake code and returns null only when it belongs to
    /// <paramref name="callerUserId"/>. Missing, invalid, expired, already-used, and wrong-user all
    /// share one message so the response cannot say the code belonged to someone else.
    /// </summary>
    internal static async Task<string?> ConsumeGoogleReauthAsync(
        ExternalLoginCodeService codes, Guid callerUserId, string? googleReauthCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(googleReauthCode))
            return GoogleReauthRequiredMessage;

        var consumed = await codes.ConsumeAsync(googleReauthCode, ct);
        if (consumed is null || consumed.Value.UserId != callerUserId)
            return GoogleReauthRequiredMessage;

        return null;
    }
}
