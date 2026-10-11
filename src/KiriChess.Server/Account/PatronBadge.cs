using System.Linq.Expressions;
using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Account;

/// <summary>
/// The single definition of "does this account currently show a badge, and which one".
///
/// It is a rule and not just a column because a one-off badge lapses on a date, with no sweep to
/// flip a bool when it does. Two shapes of the same rule live here on purpose: <see cref="For"/> for
/// an entity already in memory, and <see cref="TierExpression"/> for the leaderboard projections,
/// which must evaluate it in Postgres over hundreds of rows rather than pulling them back to filter.
/// If you change one, change the other, and the test that pins them together will say so if you did
/// not.
///
/// <para><b>A <see cref="PatronKind.Monthly"/> badge ignores <c>PatronExpiresAt</c> entirely.</b> It
/// is the live subscription that makes the badge unexpiring, not an empty column, and the two stopped
/// being the same thing when a one-off donation made <i>during</i> a subscription began banking its
/// own expiry there. Without this clause that banked date would hide the badge of somebody who is
/// still paying. See <see cref="PatronService.RevokeBySubscriptionAsync"/> for the other half: when
/// the subscription ends, the banked date is what the account falls back to instead of nothing.</para>
///
/// <para>The same rule is inlined at six call sites that project anonymous types rather than
/// <see cref="ApplicationUser"/> (<c>Games/GameNameResolver.cs</c>, <c>Games/ReplayEndpoints.cs</c>,
/// <c>Games/PublicApiEndpoints.cs</c> twice for leaderboards and once for replay names,
/// <c>Ratings/RatingEndpoints.cs</c> twice). Each carries a comment pointing here. A change to this
/// rule is a change to all eight.</para>
/// </summary>
public static class PatronBadge
{
    public static PatronTier For(ApplicationUser user, DateTimeOffset now) =>
        user.IsPatron
        && (user.PatronKind == PatronKind.Monthly || user.PatronExpiresAt is null || user.PatronExpiresAt > now)
            ? user.PatronTier
            : PatronTier.None;

    /// <summary>
    /// The same rule as <see cref="For"/>, as an expression EF Core can translate.
    /// </summary>
    public static Expression<Func<ApplicationUser, PatronTier>> TierExpression(DateTimeOffset now) =>
        u => u.IsPatron
        && (u.PatronKind == PatronKind.Monthly || u.PatronExpiresAt == null || u.PatronExpiresAt > now)
            ? u.PatronTier
            : PatronTier.None;
}
