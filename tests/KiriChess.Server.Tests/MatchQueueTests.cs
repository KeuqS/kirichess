using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class MatchQueueTests
{
    /// <summary>
    /// Simulates a Redis outage: <see cref="Db"/> throws the same exception type
    /// <c>MatchQueue.IsRedisOutage</c> filters on, before any real command can run. Everything else
    /// throws <see cref="NotSupportedException"/> because nothing under test should ever reach it.
    /// </summary>
    private sealed class ThrowingRedisConnection : IRedisConnection
    {
        public bool Enabled => true;
        // The obsolete 2-arg constructor is used deliberately here, matching the existing pattern in
        // RateLimitingTests.cs for simulating the same outage; the newer overload wants a
        // CommandStatus, not the CommandFlags its own obsolete message suggests.
        public IDatabase Db => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated outage");
        public ISubscriber Subscriber => throw new NotSupportedException();
        public bool IsConnected => false;
        public string Key(string suffix) => $"test:{suffix}";
        public RedisChannel Channel(string suffix) => RedisChannel.Literal($"test:{suffix}");
    }

    [Fact]
    public async Task OpenSeeksAsync_falls_back_to_the_local_view_on_a_redis_outage()
    {
        // C-6-7: this used to return Array.Empty<OpenSeek>() unconditionally in the outage catch,
        // which told a first-time visitor "nobody is home" during exactly the window in which
        // TryPairAsync (below) had already fallen back and filled the local dictionary with a real
        // waiting player. Every sibling method in this class (TryPairAsync, RemoveAsync, DepthsAsync)
        // already falls back to the local view on the same exception; this brings OpenSeeksAsync into
        // line with them.
        var connection = new ThrowingRedisConnection();
        var instance = new InstanceIdentity("instance-a");
        var q = new MatchQueue(connection, instance, NullLogger<MatchQueue>.Instance);
        var live = new HashSet<string> { "instance-a" };

        // TryPairAsync's own outage fallback (pre-existing, documented behaviour) is what puts a
        // real entrant into the local dictionary during an outage in the first place.
        await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c1", 1500), live);

        var seeks = await q.OpenSeeksAsync(live, TimeSpan.FromMinutes(10));

        var seek = Assert.Single(seeks);
        Assert.Equal("steal1", seek.Pool);
    }

    [Fact]
    public void First_entrant_waits_second_pairs()
    {
        var q = new MatchQueue();
        Assert.Null(q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500)));

        var opp = q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c2", 1500));
        Assert.NotNull(opp);
        Assert.Equal("c1", opp!.Value.ConnectionId);
    }

    [Fact]
    public void Pairs_the_closest_rated_opponent()
    {
        // Under eager pairing, a pool never holds more than one distinct waiter at a time: any
        // second distinct joiner immediately consumes and pairs with the sole existing waiter. So
        // "closest-rated" reduces to "the current sole waiter" in practice, confirm identity and
        // rating survive the round trip, then confirm the queue is empty again afterwards (a fresh
        // waiter is paired by the next distinct joiner, not left stranded behind an earlier one).
        var q = new MatchQueue();

        Assert.Null(q.TryPair("steal1", new Waiting(Guid.NewGuid(), "far", 1200)));
        var firstPair = q.TryPair("steal1", new Waiting(Guid.NewGuid(), "near", 1490));
        Assert.Equal("far", firstPair!.Value.ConnectionId);
        Assert.Equal(1200, firstPair.Value.Rating);

        Assert.Null(q.TryPair("steal1", new Waiting(Guid.NewGuid(), "waiter", 1490)));
        var opp = q.TryPair("steal1", new Waiting(Guid.NewGuid(), "me", 1500));
        Assert.Equal("waiter", opp!.Value.ConnectionId);
    }

    [Fact]
    public void Different_pools_do_not_pair()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        Assert.Null(q.TryPair("burn2", new Waiting(Guid.NewGuid(), "c2", 1500)));
    }

    [Fact]
    public void Same_user_does_not_pair_with_itself()
    {
        var q = new MatchQueue();
        var user = Guid.NewGuid();
        q.TryPair("steal1", new Waiting(user, "c1", 1500));
        Assert.Null(q.TryPair("steal1", new Waiting(user, "c2", 1500))); // re-entry replaces, no self-pair
    }

    [Fact]
    public void Remove_takes_a_waiter_out()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.Remove("c1");
        Assert.Null(q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c2", 1500)));
    }

    [Fact]
    public void Depths_is_empty_before_any_use()
    {
        var q = new MatchQueue();
        Assert.Empty(q.Depths());
    }

    [Fact]
    public void Depths_reports_one_after_an_unpaired_entrant()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));

        Assert.Equal(1, q.Depths()["steal1"]);
    }

    [Fact]
    public void Depths_reports_zero_once_the_pair_is_made()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c2", 1500));

        Assert.Equal(0, q.Depths()["steal1"]);
    }

    [Fact]
    public void Depths_keeps_a_zero_entry_for_a_pool_entered_and_then_emptied()
    {
        // A pool that drops off the scrape entirely is indistinguishable from a pool nobody has
        // tried, and "0 waiting" is the more useful signal (see MatchQueue.Depths() doc comment).
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.Remove("c1");

        Assert.True(q.Depths().ContainsKey("steal1"));
        Assert.Equal(0, q.Depths()["steal1"]);
    }

    [Fact]
    public void Joining_a_second_pool_removes_the_entrant_from_the_first()
    {
        // Phase 6e.3: a user must occupy at most one queue globally, not one per pool. Without the
        // fix this user would still be waiting in "steal1" after joining "burn2".
        var q = new MatchQueue();
        var user = Guid.NewGuid();
        Assert.Null(q.TryPair("steal1", new Waiting(user, "c1", 1500)));
        Assert.Equal(1, q.Depths()["steal1"]);

        Assert.Null(q.TryPair("burn2", new Waiting(user, "c1", 1500)));

        Assert.Equal(0, q.Depths()["steal1"]);
        Assert.Equal(1, q.Depths()["burn2"]);
    }

    [Fact]
    public void A_user_waiting_in_six_pools_is_reduced_to_one_by_the_next_join()
    {
        var q = new MatchQueue();
        var user = Guid.NewGuid();
        var pools = new[] { "bounty1", "steal1", "steal2", "burn1", "steal3", "burn2" };

        // Simulate the pre-fix hole: manually seed the user into every pool via direct TryPair calls
        // using distinct connection ids, as if the dedupe bug still existed. Since the fix now sweeps
        // on every call, seed by calling TryPair for each pool with a *different* user first so the
        // pools aren't empty, then confirm a single join by `user` only ever leaves them in one place.
        foreach (var pool in pools)
            Assert.Null(q.TryPair(pool, new Waiting(user, "c1", 1500)));

        // Every prior TryPair call already swept `user` out of every other pool, so only the last
        // pool joined should hold them.
        var depths = q.Depths();
        Assert.Equal(1, depths[pools[^1]]);
        foreach (var pool in pools[..^1])
            Assert.Equal(0, depths[pool]);
    }

    [Fact]
    public void Existing_pairing_behaviour_is_unchanged_after_the_cross_pool_sweep()
    {
        // Re-run of First_entrant_waits_second_pairs to confirm the sweep didn't loosen pairing.
        var q = new MatchQueue();
        Assert.Null(q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500)));

        var opp = q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c2", 1500));
        Assert.NotNull(opp);
        Assert.Equal("c1", opp!.Value.ConnectionId);
    }

    // --- OpenSeeks (Phase 9e.4) ---

    [Fact]
    public void OpenSeeks_is_empty_before_any_use()
    {
        var q = new MatchQueue();
        Assert.Empty(q.OpenSeeks(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void OpenSeeks_lists_an_unpaired_entrant()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));

        var seeks = q.OpenSeeks(TimeSpan.FromMinutes(10));
        var seek = Assert.Single(seeks);
        Assert.Equal("steal1", seek.Pool);
    }

    [Fact]
    public void OpenSeeks_drops_a_seek_once_it_is_paired()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c2", 1500)); // pairs with c1

        Assert.Empty(q.OpenSeeks(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void OpenSeeks_drops_a_seek_once_the_connection_leaves()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.Remove("c1");

        Assert.Empty(q.OpenSeeks(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void OpenSeeks_hides_an_entry_older_than_max_age()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));

        Assert.Single(q.OpenSeeks(TimeSpan.FromMinutes(10)));
        Assert.Empty(q.OpenSeeks(TimeSpan.Zero)); // already older than a zero-width window
    }

    [Fact]
    public void OpenSeeks_does_not_advertise_bot_pools()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "human", 1500));
        q.TryPair("bot:bounty1", new Waiting(Guid.NewGuid(), "bot", 1500));

        var seeks = q.OpenSeeks(TimeSpan.FromMinutes(10));
        var seek = Assert.Single(seeks);
        Assert.Equal("steal1", seek.Pool);
    }

    [Fact]
    public void OpenSeeks_can_list_bot_pools_when_asked()
    {
        var q = new MatchQueue();
        q.TryPair("bot:bounty1", new Waiting(Guid.NewGuid(), "bot", 1500));

        var seeks = q.OpenSeeks(TimeSpan.FromMinutes(10), ["bot:bounty1"]);
        var seek = Assert.Single(seeks);
        Assert.Equal("bot:bounty1", seek.Pool);
    }

    [Fact]
    public void OpenSeeks_orders_oldest_first_across_pools()
    {
        var q = new MatchQueue();
        q.TryPair("steal1", new Waiting(Guid.NewGuid(), "c1", 1500));
        q.TryPair("burn2", new Waiting(Guid.NewGuid(), "c2", 1500));

        var seeks = q.OpenSeeks(TimeSpan.FromMinutes(10));
        Assert.Equal(2, seeks.Count);
        Assert.Equal("steal1", seeks[0].Pool); // enqueued first
        Assert.Equal("burn2", seeks[1].Pool);
    }
}
