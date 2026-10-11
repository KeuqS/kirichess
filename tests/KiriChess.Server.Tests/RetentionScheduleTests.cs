using KiriChess.Server.Retention;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// <see cref="RetentionSchedule"/>'s cutoff arithmetic against literal values - the part of Phase
/// 8a.3 that can be wrong by a factor of a thousand (days vs. hours vs. minutes) and would otherwise
/// only surface against a real database. No Docker, always executes.
/// </summary>
public sealed class RetentionScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Schedule_has_exactly_the_six_swept_rules()
    {
        var rules = RetentionSchedule.From(new RetentionOptions());

        Assert.Equal(6, rules.Count);
        Assert.Contains(rules, r => r.Name == RetentionRules.RefreshTokens);
        Assert.Contains(rules, r => r.Name == RetentionRules.ExternalLoginCodes);
        Assert.Contains(rules, r => r.Name == RetentionRules.TwoFactorChallenges);
        Assert.Contains(rules, r => r.Name == RetentionRules.Challenges);
        Assert.Contains(rules, r => r.Name == RetentionRules.ApiTokens);
        // S-3-1. The one rule that clears a column rather than deleting a row; it is in the schedule
        // because its lifetime is a retention decision, not because it deletes something.
        Assert.Contains(rules, r => r.Name == RetentionRules.BanEvasionMarkers);
    }

    [Fact]
    public void Ban_evasion_marker_grace_is_configurable_and_flows_through_to_the_cutoff()
    {
        var options = new RetentionOptions { BanEvasionMarkerGraceDays = 45 };
        var rule = RetentionSchedule.From(options).Single(r => r.Name == RetentionRules.BanEvasionMarkers);

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(Now.AddDays(-45), cutoff);
    }

    [Fact]
    public void TwoFactorChallenge_grace_is_configurable_and_flows_through_to_the_cutoff()
    {
        var options = new RetentionOptions { TwoFactorChallengeGraceDays = 2 };
        var rule = RetentionSchedule.From(options).Single(r => r.Name == RetentionRules.TwoFactorChallenges);

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(Now.AddDays(-2), cutoff);
    }

    [Fact]
    public void CutoffFor_subtracts_the_grace_from_now()
    {
        var options = new RetentionOptions { RefreshTokenGraceDays = 30 };
        var rule = RetentionSchedule.From(options).Single(r => r.Name == RetentionRules.RefreshTokens);

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(Now.AddDays(-30), cutoff);
    }

    [Fact]
    public void Refresh_token_grace_is_measured_in_days_not_hours_or_minutes()
    {
        // The factor-of-a-thousand mistake: a grace read as hours or minutes would still compile and
        // still pass a test that only checks the cutoff is "in the past".
        var options = new RetentionOptions { RefreshTokenGraceDays = 30 };
        var rule = RetentionSchedule.From(options).Single(r => r.Name == RetentionRules.RefreshTokens);

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(TimeSpan.FromDays(30), Now - cutoff);
        Assert.NotEqual(TimeSpan.FromHours(30), Now - cutoff);
        Assert.NotEqual(TimeSpan.FromMinutes(30), Now - cutoff);
    }

    [Fact]
    public void Grace_is_configurable_and_flows_through_to_the_cutoff()
    {
        var options = new RetentionOptions { RefreshTokenGraceDays = 7 };
        var rule = RetentionSchedule.From(options).Single(r => r.Name == RetentionRules.RefreshTokens);

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(Now.AddDays(-7), cutoff);
    }

    [Fact]
    public void Zero_grace_is_a_same_instant_cutoff_not_an_exception()
    {
        // GracePastExpiry = TimeSpan.Zero means "as soon as expired", which is legal - not a
        // misconfiguration to guard against.
        var rule = new RetentionRule("test-rule", TimeSpan.Zero, "test reason");

        var cutoff = RetentionSchedule.CutoffFor(rule, Now);

        Assert.Equal(Now, cutoff);
    }

    [Fact]
    public void Every_rule_has_a_non_empty_reason()
    {
        var rules = RetentionSchedule.From(new RetentionOptions());

        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }
}
