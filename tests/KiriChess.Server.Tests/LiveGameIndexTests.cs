using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// I-1-3 and I-1-5: seating is once per game, not once per publish; <c>games:live</c> prunes by
/// expiry score past the read window; <c>/account/playing</c> reads a per-player set. Real Redis
/// via <see cref="RedisFixture"/>; each test uses its own prefix.
/// </summary>
public sealed class LiveGameIndexTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public LiveGameIndexTests(RedisFixture fixture) => _fixture = fixture;

    private static KiriChessConfig SimpleConfig() =>
        new() { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };

    private static GameSession NewSession(Guid? white = null, Guid? black = null) =>
        new(new KiriChessEngine(), SimpleConfig(), white ?? Guid.NewGuid(), black ?? Guid.NewGuid(), "bounty1", isRated: true);

    private (RedisConnection Connection, LiveGameIndex Index) CreateIndex(string prefix)
    {
        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);
        var identity = new InstanceIdentity("instance-a");
        var connection = new RedisConnection(options, identity);
        var index = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        return (connection, index);
    }

    [Fact]
    public async Task Disabled_redis_is_a_silent_no_op()
    {
        var options = RedisOptions.From(new ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        var identity = new InstanceIdentity("solo");
        using var connection = new RedisConnection(options, identity);
        var index = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        var session = NewSession();

        await index.PublishAsync(session, "Alice", "Bob", "None", "None");
        await index.SeatPlayersAsync(session);
        await index.RemoveAsync(session);

        Assert.Empty(await index.LiveGamesAsync(50));
        Assert.Empty(await index.PlayingGamesAsync(session.WhitePlayerId));
        Assert.Equal(0, await index.PlayersInGameCountAsync());
        Assert.False(await index.IsPlayerInGameAsync(session.WhitePlayerId));
    }

    [SkippableFact]
    public async Task PublishAsync_does_not_seat_players_and_a_second_publish_does_not_grow_the_set()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, index) = CreateIndex(prefix);
        using (connection)
        {
            var session = NewSession();
            await index.PublishAsync(session, "Alice", "Bob", "None", "None");

            Assert.Equal(0, await index.PlayersInGameCountAsync());
            Assert.False(await index.IsPlayerInGameAsync(session.WhitePlayerId));
            Assert.Equal(0, await connection.Db.SetLengthAsync(connection.Key("players:ingame")));

            await index.SeatPlayersAsync(session);
            Assert.Equal(2, await index.PlayersInGameCountAsync());
            Assert.True(await index.IsPlayerInGameAsync(session.WhitePlayerId));
            Assert.True(await index.IsPlayerInGameAsync(session.BlackPlayerId));

            await index.PublishAsync(session, "Alice", "Bob", "None", "None");
            Assert.Equal(2, await index.PlayersInGameCountAsync());

            await index.SeatPlayersAsync(session);
            Assert.Equal(2, await index.PlayersInGameCountAsync());
        }
    }

    [SkippableFact]
    public async Task LiveGamesAsync_prunes_an_expired_score_that_sits_past_the_read_limit()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, index) = CreateIndex(prefix);
        using (connection)
        {
            var liveKey = connection.Key("games:live");
            var staleId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await connection.Db.SortedSetAddAsync(liveKey, staleId, now - 60);

            // Future-scored members that would occupy a small read window so the stale id would
            // never have been visited by the old in-window ZREM.
            for (var i = 0; i < 8; i++)
                await connection.Db.SortedSetAddAsync(liveKey, Guid.NewGuid().ToString(), now + 10_000);

            await index.LiveGamesAsync(limit: 1);

            Assert.Null(await connection.Db.SortedSetScoreAsync(liveKey, staleId));
        }
    }

    [SkippableFact]
    public async Task PlayingGamesAsync_returns_the_subjects_game_when_the_live_scan_window_does_not()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, index) = CreateIndex(prefix);
        using (connection)
        {
            var me = Guid.NewGuid();
            var session = NewSession(white: me);
            await index.PublishAsync(session, "Alice", "Bob", "None", "None");
            await index.SeatPlayersAsync(session);

            var liveKey = connection.Key("games:live");
            var highScore = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100_000;
            for (var i = 0; i < 201; i++)
                await connection.Db.SortedSetAddAsync(liveKey, Guid.NewGuid().ToString(), highScore);

            var viaLive = await index.LiveGamesAsync(200);
            Assert.DoesNotContain(viaLive, g => g.GameId == session.Id);

            var playing = await index.PlayingGamesAsync(me);
            var row = Assert.Single(playing);
            Assert.Equal(session.Id, row.GameId);
            Assert.Equal(me, row.WhitePlayerId);
        }
    }

    [SkippableFact]
    public async Task RemoveAsync_clears_seats_and_the_per_player_index()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, index) = CreateIndex(prefix);
        using (connection)
        {
            var session = NewSession();
            await index.PublishAsync(session, "Alice", "Bob", "None", "None");
            await index.SeatPlayersAsync(session);
            await index.RemoveAsync(session);

            Assert.Equal(0, await index.PlayersInGameCountAsync());
            Assert.False(await index.IsPlayerInGameAsync(session.WhitePlayerId));
            Assert.Empty(await index.PlayingGamesAsync(session.WhitePlayerId));
            Assert.Empty(await index.LiveGamesAsync(50));
            Assert.Equal(0, await connection.Db.SetLengthAsync(connection.Key($"player:games:{session.WhitePlayerId}")));
        }
    }
}
