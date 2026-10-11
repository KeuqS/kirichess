namespace KiriChess.Server.Infrastructure.Push;

/// <summary><c>GET /api/push/vapid-public-key</c>'s body. <see cref="PublicKey"/> is null when push is not configured, and the frontend hides the enable-push control entirely in that case.</summary>
public sealed record VapidPublicKeyDto(bool Enabled, string? PublicKey);

/// <summary>
/// <c>POST /api/push/subscribe</c>'s body: the fields of the browser's own
/// <c>PushSubscription.toJSON()</c>, flattened rather than nested under <c>keys</c> - there is exactly
/// one caller (the frontend maps the nested shape itself) and every other request DTO in this codebase
/// is flat.
/// </summary>
public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth);

/// <summary><c>POST /api/push/unsubscribe</c>'s body.</summary>
public sealed record PushUnsubscribeRequest(string Endpoint);
