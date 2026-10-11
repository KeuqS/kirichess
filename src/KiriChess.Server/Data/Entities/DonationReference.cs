namespace KiriChess.Server.Data.Entities;

/// <summary>
/// The handshake that lets a Stripe payment find a KiriChess account.
///
/// The donate buttons are Stripe payment links, which accept exactly one caller-supplied value,
/// <c>client_reference_id</c>, and hand it back on the webhook. The obvious thing to put there is
/// the account's own Guid, and this table exists so we do not: that id would then sit in browser
/// history, in the Referer header, in Stripe's dashboard and in Stripe's logs, and it would be
/// forgeable by anyone willing to spend money attaching a badge to someone else's account.
///
/// So the signed-in browser asks for one of these first. <see cref="Reference"/> is random, means
/// nothing outside this table, dies in 24 hours, and is consumed the first time a webhook redeems
/// it. Stripe never receives an account identifier, which is also the honest answer for PRIVACY and
/// the ROPA.
///
/// Unlike <c>RefreshToken</c> and <c>ExternalLoginCode</c> this is stored in plain text rather than
/// hashed: it is not a credential, it grants nothing on its own, and the webhook has to look it up
/// by the exact value Stripe echoes back.
/// </summary>
public sealed class DonationReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>
    /// 32 lowercase hex characters (a Guid in "N" format). Stripe restricts
    /// <c>client_reference_id</c> to alphanumerics, dashes and underscores, 200 characters at most,
    /// so this format is chosen to sit safely inside that.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Non-null once a webhook has redeemed it. Single use, by design.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
