using System.Text.Json;
using System.Text.Json.Serialization;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 6e.2: proves the per-ply think-time telemetry (<c>ThinkMs</c>/<c>ReceivedAtUtc</c>) is
/// captured off the same injectable wall clock <see cref="GameSession"/> already uses, that it does
/// not perturb the clock economy the engine actually charges, and that rows persisted before 6e.2
/// still deserialize (the whole reason no data migration was needed).
/// </summary>
public sealed class MoveTelemetryTests
{
    private static KiriChessConfig CreateTestConfig() => new KiriChessConfig
    {
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0
    };

    private static (GameSession session, FakeGameClock clock) CreateSession(
        Guid whiteId, Guid blackId, KiriChessConfig? config = null, string? pool = "bounty1", bool rated = true)
    {
        var clock = new FakeGameClock();
        var cfg = config ?? CreateTestConfig();
        var session = new GameSession(new KiriChessEngine(), cfg, whiteId, blackId, pool, rated, clock);
        return (session, clock);
    }

    [Fact]
    public void Advancing_the_clock_between_moves_records_think_ms_on_the_next_move()
    {
        var soloId = Guid.NewGuid();
        var (session, clock) = CreateSession(soloId, soloId);

        session.ApplyMove(soloId, Move.Parse("e2e4")); // no time advance since CreatedAt: ThinkMs 0
        clock.Advance(TimeSpan.FromMilliseconds(2500));
        session.ApplyMove(soloId, Move.Parse("e7e5"));

        Assert.Equal(0, session.Moves[0].ThinkMs);
        Assert.Equal(2500, session.Moves[1].ThinkMs);
    }

    [Fact]
    public void ReceivedAtUtc_equals_the_clock_reading_at_apply_time()
    {
        // Load-bearing for the 6f.1 split: this proves ReceivedAtUtc stayed on the wall clock even
        // though ElapsedSeconds/ThinkMs moved to the monotonic source.
        var soloId = Guid.NewGuid();
        var (session, clock) = CreateSession(soloId, soloId);

        clock.Advance(TimeSpan.FromSeconds(3));
        session.ApplyMove(soloId, Move.Parse("e2e4"));

        Assert.Equal(clock.UtcNow, session.Moves[0].ReceivedAtUtc);
    }

    [Fact]
    public void Telemetry_does_not_change_the_clock_economy()
    {
        // Regression guard: the engine still receives whole-second think time and the persisted
        // clock deltas/after-values are exactly what they were before ThinkMs/ReceivedAtUtc existed.
        var soloId = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        var (session, clock) = CreateSession(soloId, soloId, config);

        clock.Advance(TimeSpan.FromSeconds(10));
        var result = session.ApplyMove(soloId, Move.Parse("e2e4"));

        Assert.Equal(170, session.Position.WhiteTimeSeconds);
        Assert.Equal(180, session.Position.BlackTimeSeconds);
        // WhiteTimeDelta/BlackTimeDelta are the economy-only deltas (captures/checks/move cost),
        // think-time is subtracted separately (KiriChessEngine.Apply) and does not appear here. This
        // test's config has no economy events, so both are zero even though White's clock dropped.
        Assert.Equal(0, result.WhiteTimeDelta);
        Assert.Equal(0, result.BlackTimeDelta);

        var record = session.Moves[0];
        Assert.Equal(170, record.WhiteTimeAfter);
        Assert.Equal(180, record.BlackTimeAfter);
        Assert.Equal(0, record.WhiteDelta);
        Assert.Equal(0, record.BlackDelta);
        // Wall-clock telemetry moved to the millisecond figure; the engine-facing second figure did not.
        Assert.Equal(10_000, record.ThinkMs);
    }

    [Fact]
    public void Old_shape_json_without_telemetry_fields_deserializes_with_defaults()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        const string oldShapeJson =
            """{"Ply":1,"Uci":"e2e4","Mover":"White","WhiteTimeAfter":170,"BlackTimeAfter":180,"WhiteDelta":-10,"BlackDelta":0,"Status":"Ongoing"}""";

        var record = JsonSerializer.Deserialize<MoveRecord>(oldShapeJson, options);

        Assert.NotNull(record);
        Assert.Equal(0, record!.ThinkMs);
        Assert.Null(record.ReceivedAtUtc);
    }
}
