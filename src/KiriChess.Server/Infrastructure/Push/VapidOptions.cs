namespace KiriChess.Server.Infrastructure.Push;

/// <summary>
/// Web push configuration (11b.1 follow-up). Same shape as <c>EmailOptions</c>: a plain record with a
/// <see cref="From"/> factory, so the enabled/disabled decision is a single readable expression rather
/// than scattered null checks at the call sites.
/// </summary>
/// <param name="PublicKey">The VAPID public key, handed to browsers as <c>applicationServerKey</c>. Not secret.</param>
/// <param name="PrivateKey">The VAPID private key. Signs every push request; never sent to a browser.</param>
/// <param name="Subject">A <c>mailto:</c> or <c>https:</c> URI identifying the sender, required by the push protocol.</param>
public sealed record VapidOptions(string PublicKey, string PrivateKey, string Subject)
{
    /// <summary>
    /// Fails closed exactly like <c>EmailOptions.Enabled</c>: without all three values no push is ever
    /// attempted, <see cref="PushNotificationService"/> becomes a silent no-op, and the deploy has zero
    /// egress and zero startup cost. Generate a pair with <c>npx web-push generate-vapid-keys</c>.
    /// </summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);

    public static VapidOptions From(IConfiguration c) => new(
        c.GetValue("Vapid:PublicKey", string.Empty) ?? string.Empty,
        c.GetValue("Vapid:PrivateKey", string.Empty) ?? string.Empty,
        c.GetValue("Vapid:Subject", string.Empty) ?? string.Empty);
}
