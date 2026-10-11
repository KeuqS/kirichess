namespace KiriChess.Server.Account;

/// <summary>
/// Which donation thank-you tier a patron holds. The five names mirror
/// the amount table in ui/components/DonationTiers.tsx and are not shown
/// in the UI: public surfaces show one heart regardless of this value.
/// Persisted as a string so inserting a tier later never renumbers an
/// existing row.
/// </summary>
public enum PatronTier
{
    None = 0,
    Pawn,
    Knight,
    Bishop,
    Rook,
    Queen,
}

/// <summary>Whether a badge came from a single payment or from a live subscription.</summary>
public enum PatronKind
{
    None = 0,
    OneTime,
    Monthly,
}

/// <summary>
/// Maps a Stripe payment amount to a tier. Deliberately threshold based rather than exact match:
/// ten of the eleven payment links are fixed at 5/10/15/25/50 EUR, but the eleventh lets the donor
/// type any amount, and an exact-match table would silently grant that donor nothing.
/// </summary>
public static class PatronTiers
{
    // Highest first. Any successful payment is worth at least a Pawn: the badge is a thank-you, not
    // a purchase, and refusing one to a 3 EUR donor buys nothing.
    private static readonly (long MinCents, PatronTier Tier)[] Thresholds =
    [
        (5000, PatronTier.Queen),
        (2500, PatronTier.Rook),
        (1500, PatronTier.Bishop),
        (1000, PatronTier.Knight),
        (1,    PatronTier.Pawn),
    ];

    public static PatronTier FromAmountCents(long amountCents)
    {
        foreach (var (min, tier) in Thresholds)
        {
            if (amountCents >= min) return tier;
        }

        return PatronTier.None;
    }
}
