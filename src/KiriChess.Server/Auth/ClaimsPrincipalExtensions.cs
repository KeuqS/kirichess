using System.Security.Claims;

namespace KiriChess.Server.Auth;

internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// User id from the access-token <c>NameIdentifier</c> or <c>sub</c> claim. Null when the
    /// principal is missing, anonymous, or the claim is not a GUID.
    /// </summary>
    public static Guid? GetUserId(this ClaimsPrincipal? user)
    {
        var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
