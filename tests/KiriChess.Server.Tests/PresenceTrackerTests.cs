using KiriChess.Server.Games;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class PresenceTrackerTests
{
    [Fact]
    public void Counts_distinct_users_not_connections()
    {
        var tracker = new PresenceTracker();
        var user = Guid.NewGuid();

        // Same person with two tabs open is one player online.
        tracker.Track("c1", user);
        tracker.Track("c2", user);
        Assert.Equal(1, tracker.OnlineCount);

        tracker.Track("c3", Guid.NewGuid());
        Assert.Equal(2, tracker.OnlineCount);
    }

    [Fact]
    public void User_stays_online_until_their_last_connection_drops()
    {
        var tracker = new PresenceTracker();
        var user = Guid.NewGuid();
        tracker.Track("c1", user);
        tracker.Track("c2", user);

        tracker.Remove("c1");
        Assert.Equal(1, tracker.OnlineCount);

        tracker.Remove("c2");
        Assert.Equal(0, tracker.OnlineCount);
    }

    [Fact]
    public void Removing_an_untracked_connection_is_a_no_op()
    {
        var tracker = new PresenceTracker();
        tracker.Track("c1", Guid.NewGuid());

        tracker.Remove("anonymous-visitor"); // never tracked, anonymous connections aren't counted
        Assert.Equal(1, tracker.OnlineCount);
    }
}
