using KiriChess.Server.Retention;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers <see cref="DormantAccountPolicy.Classify"/>: the pure Phase 8a.3 dormancy predicate. Pure
/// and synchronous, so this needs no Docker and no database - literal values in, a reason (or null)
/// out.
/// </summary>
public sealed class DormantAccountPolicyTests
{
    private const int InactiveDays = 365;
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now - TimeSpan.FromDays(InactiveDays);

    private static DormancyFact Fact(
        DateTimeOffset createdAt,
        bool emailConfirmed = true,
        int gamesPlayed = 0,
        DateTimeOffset? lastGameAt = null,
        bool hasSanctions = false) =>
        new(Guid.NewGuid(), "Player", createdAt, emailConfirmed, gamesPlayed, lastGameAt, hasSanctions);

    [Fact]
    public void Account_younger_than_the_window_is_never_dormant_even_with_zero_games()
    {
        var fact = Fact(createdAt: Now.AddDays(-1), emailConfirmed: false, gamesPlayed: 0);

        Assert.Null(DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void Old_unconfirmed_no_games_is_never_activated()
    {
        var fact = Fact(createdAt: Cutoff.AddDays(-1), emailConfirmed: false, gamesPlayed: 0);

        Assert.Equal(DormancyReasons.NeverActivated, DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void Old_confirmed_no_games_is_never_played()
    {
        var fact = Fact(createdAt: Cutoff.AddDays(-1), emailConfirmed: true, gamesPlayed: 0);

        Assert.Equal(DormancyReasons.NeverPlayed, DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void Old_account_with_a_recent_game_is_not_dormant()
    {
        var fact = Fact(
            createdAt: Cutoff.AddDays(-100),
            gamesPlayed: 5,
            lastGameAt: Cutoff.AddDays(1));

        Assert.Null(DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void Old_account_whose_last_game_is_before_the_window_is_inactive()
    {
        var fact = Fact(
            createdAt: Cutoff.AddDays(-100),
            gamesPlayed: 5,
            lastGameAt: Cutoff.AddDays(-1));

        Assert.Equal(DormancyReasons.Inactive, DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void CreatedAt_exactly_at_the_cutoff_is_not_dormant()
    {
        // CreatedAt >= cutoff excludes the account - the boundary is inclusive of "not dormant".
        var fact = Fact(createdAt: Cutoff, emailConfirmed: false, gamesPlayed: 0);

        Assert.Null(DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void LastGameAt_exactly_at_the_cutoff_is_not_dormant()
    {
        // LastGameAt < cutoff is required to flag - exactly-at-cutoff does not qualify.
        var fact = Fact(createdAt: Cutoff.AddDays(-100), gamesPlayed: 3, lastGameAt: Cutoff);

        Assert.Null(DormantAccountPolicy.Classify(fact, Now, InactiveDays));
    }

    [Fact]
    public void HasSanctions_changes_nothing_about_the_verdict()
    {
        var withoutSanctions = Fact(createdAt: Cutoff.AddDays(-1), emailConfirmed: false, hasSanctions: false);
        var withSanctions = Fact(createdAt: Cutoff.AddDays(-1), emailConfirmed: false, hasSanctions: true);

        Assert.Equal(
            DormantAccountPolicy.Classify(withoutSanctions, Now, InactiveDays),
            DormantAccountPolicy.Classify(withSanctions, Now, InactiveDays));
    }

    [Fact]
    public void The_window_is_in_days_not_some_other_unit()
    {
        var justOverTheWindow = Fact(createdAt: Now.AddDays(-366), emailConfirmed: false, gamesPlayed: 0);
        var justUnderTheWindow = Fact(createdAt: Now.AddDays(-364), emailConfirmed: false, gamesPlayed: 0);

        Assert.NotNull(DormantAccountPolicy.Classify(justOverTheWindow, Now, InactiveDays));
        Assert.Null(DormantAccountPolicy.Classify(justUnderTheWindow, Now, InactiveDays));
    }
}
