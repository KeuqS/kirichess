namespace KiriChess.Server.Data.Entities;

/// <summary>
/// One browser's push subscription (11b.1 follow-up). Named <c>WebPushSubscription</c> rather than
/// <c>PushSubscription</c> to avoid colliding with <c>WebPush.PushSubscription</c>, the third-party
/// type this project's push sender maps it to at the point of sending.
///
/// A user can hold several rows at once (one per device/browser they enabled push on), which is why
/// this is its own table rather than columns on <see cref="ApplicationUser"/> the way the 11b.1 mail
/// preference is - unlike a mail category, "subscribed" is not a single yes/no per account.
/// </summary>
public sealed class WebPushSubscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The push service URL a message is POSTed to. Unique: re-subscribing the same browser reassigns this row rather than duplicating it.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The subscription's P-256 ECDH public key, base64url, used to encrypt the message payload.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The subscription's auth secret, base64url, also used to encrypt the message payload.</summary>
    public string Auth { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
