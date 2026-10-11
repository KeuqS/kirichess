namespace KiriChess.Server.Account;

/// <summary>
/// Public avatar URLs are same-API paths, never an off-site address. The <c>v</c> query is the
/// upload instant so a CDN or browser that cached the previous JPEG fetches the new one.
/// </summary>
public static class AvatarUrls
{
    public static string? For(Guid userId, DateTimeOffset? updatedAt) =>
        updatedAt is null
            ? null
            : $"/api/users/{userId:D}/avatar?v={updatedAt.Value.ToUnixTimeMilliseconds()}";
}
