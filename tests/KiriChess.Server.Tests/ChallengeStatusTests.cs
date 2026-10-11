using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// <see cref="ChallengeService.StatusOf"/> and <see cref="ChallengeService.NewToken"/> against literal
/// values - the pure half of Phase 9d.8, kept in its own Docker-free file so
/// <see cref="ChallengeTests"/> (which needs a real Postgres for the accept race and the eligibility
/// checks) stays focused on the end-to-end paths. No Docker, always executes.
/// </summary>
public sealed class ChallengeStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static Challenge NewChallenge() => new()
    {
        Token = ChallengeService.NewToken(),
        ChallengerId = Guid.NewGuid(),
        Pool = "steal1",
        Colour = ChallengeColour.Random,
        CreatedAt = Now.AddMinutes(-30),
        ExpiresAt = Now.AddMinutes(30),
    };

    [Fact]
    public void Open_challenge_with_no_flags_set_reads_as_Open()
    {
        var c = NewChallenge();

        Assert.Equal(ChallengeStatus.Open, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void Past_ExpiresAt_with_no_other_flag_reads_as_Expired()
    {
        var c = NewChallenge();
        c.ExpiresAt = Now.AddMinutes(-1);

        Assert.Equal(ChallengeStatus.Expired, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void AcceptedAt_set_reads_as_Accepted()
    {
        var c = NewChallenge();
        c.AcceptedAt = Now.AddMinutes(-5);
        c.AcceptedById = Guid.NewGuid();

        Assert.Equal(ChallengeStatus.Accepted, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void CancelledAt_set_reads_as_Cancelled()
    {
        var c = NewChallenge();
        c.CancelledAt = Now.AddMinutes(-5);

        Assert.Equal(ChallengeStatus.Cancelled, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void Status_precedence_prefers_cancelled_over_expired()
    {
        // A row that was cancelled and has since also aged past its own ExpiresAt must still read as
        // Cancelled - that is the truthful account of what actually happened to it (TASK-A step 5).
        var c = NewChallenge();
        c.CancelledAt = Now.AddMinutes(-40);
        c.ExpiresAt = Now.AddMinutes(-1);

        Assert.Equal(ChallengeStatus.Cancelled, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void Status_precedence_prefers_accepted_over_expired()
    {
        // An accepted row read back after its own TTL has passed still reads as Accepted - the game it
        // produced is what matters by then, not the link's own clock.
        var c = NewChallenge();
        c.AcceptedAt = Now.AddMinutes(-40);
        c.AcceptedById = Guid.NewGuid();
        c.ExpiresAt = Now.AddMinutes(-1);

        Assert.Equal(ChallengeStatus.Accepted, ChallengeService.StatusOf(c, Now));
    }

    [Fact]
    public void NewToken_is_22_url_safe_characters()
    {
        var token = ChallengeService.NewToken();

        Assert.Equal(22, token.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void NewToken_is_not_predictable_across_calls()
    {
        var a = ChallengeService.NewToken();
        var b = ChallengeService.NewToken();

        Assert.NotEqual(a, b);
    }
}
