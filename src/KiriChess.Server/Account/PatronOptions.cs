namespace KiriChess.Server.Account;

/// <summary>
/// The one patron-badge decision that is a product call rather than a mechanical one, kept in config
/// so it moves without a rebuild: how long a single donation's badge lasts.
///
/// A monthly subscription needs no answer, Stripe tells us when it ends. A one-off payment has
/// nothing to cancel, so it needs a stated life. One month, flat, regardless of amount: that is
/// what the donate page tells a donor, and a second one-off stacks another month onto whatever
/// time is left. Set Patron:OneTimeGrantMonths to change the length without a rebuild.
///
/// A longer flat life was the earlier default, because docs/audits/L.md (L-7-1) wants the badge to
/// read as a thank-you rather than a purchase. Raising this value is still that lever.
/// </summary>
public sealed record PatronOptions(int OneTimeGrantMonths)
{
    public const string SectionName = "Patron";

    public static PatronOptions From(IConfiguration c) => new(
        Math.Clamp(c.GetValue("Patron:OneTimeGrantMonths", 1), 1, 120));
}
