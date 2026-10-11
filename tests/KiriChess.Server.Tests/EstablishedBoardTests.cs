using KiriChess.Server.Ratings;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// C-7-1's membership rule, with a fixed clock. The endpoint and the cache both call this; the
/// boundary belongs here so neither of those fixtures has to sleep up to it.
/// </summary>
public sealed class EstablishedBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = RatingConstants.LeaderboardActiveWindow;

    [Fact]
    public void A_settled_player_inside_the_window_is_on_the_board()
    {
        Assert.True(EstablishedBoard.Contains(80, Now - Window, Now));
        Assert.True(EstablishedBoard.Contains(RatingConstants.ProvisionalRdThreshold, Now, Now));
    }

    [Fact]
    public void A_settled_player_one_tick_past_the_window_is_off_the_board()
    {
        Assert.False(EstablishedBoard.Contains(48, Now - Window - TimeSpan.FromTicks(1), Now));
    }

    [Fact]
    public void A_recent_provisional_rating_is_off_the_board()
    {
        Assert.False(EstablishedBoard.Contains(RatingConstants.ProvisionalRdThreshold + 0.1, Now, Now));
    }
}
