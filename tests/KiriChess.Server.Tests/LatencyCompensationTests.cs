using System.Text.Json;
using System.Text.Json.Serialization;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 6f.2: proves the server-measured, EMA-smoothed, capped latency credit is charged in exactly
/// one place (<c>ChargedElapsed</c>, private) and therefore reaches both move-charging and flag-fall,
/// that a client can never claim a credit it was not measured for, that the credit cannot exceed the
/// configured cap regardless of how bad a single sample is, and that <c>MoveRecord.ThinkMs</c> keeps
/// its pre-6f.2 raw meaning while <c>LagMs</c> carries the new figure alongside it.
/// </summary>
public sealed class LatencyCompensationTests
{
    private static KiriChessConfig CreateTestConfig() => new KiriChessConfig
    {
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0
    };

    private static (GameSession session, FakeGameClock clock) CreateSession(
        Guid whiteId, Guid blackId, KiriChessConfig? config = null,
        LagCompensationOptions? lag = null, string? pool = "bounty1", bool rated = true)
    {
        var clock = new FakeGameClock();
        var cfg = config ?? CreateTestConfig();
        var session = new GameSession(new KiriChessEngine(), cfg, whiteId, blackId, pool, rated, clock, lag);
        return (session, clock);
    }

    [Fact]
    public void No_measurement_means_no_compensation()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(10));
        session.ApplyMove(white, Move.Parse("e2e4"));

        // Nobody was ever probed, so the mover is charged exactly the raw elapsed, the bot/practice/
        // HTTP-move-endpoint path, none of which have a connection to measure.
        Assert.Equal(170, session.Position.WhiteTimeSeconds);
    }

    [Fact]
    public void Measured_round_trip_is_credited_to_the_mover()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        var (session, clock) = CreateSession(white, black, config);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(400));
        // 9.2s raw, minus a 400ms credit, is 8.8s: floor(9.2) = 9 but floor(8.8) = 8. Picked
        // deliberately so the credit crosses a whole-second boundary; that is the only place a
        // sub-second credit is observable at all on a whole-second clock.
        clock.Advance(TimeSpan.FromSeconds(9.2));
        session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(172, session.Position.WhiteTimeSeconds); // 180 - 8, not 180 - 9
    }

    [Fact]
    public void Compensation_is_capped()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(3000));

        Assert.Equal(500, session.CompensationMs(Side.White));
    }

    [Fact]
    public void A_single_outlier_sample_does_not_dominate_the_estimate()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        // A generous cap so this test observes the EMA itself rather than the 500ms default clamp.
        var lag = new LagCompensationOptions { MaxCompensationMs = 10_000 };
        var (session, _) = CreateSession(white, black, lag: lag);

        for (var i = 0; i < 5; i++) session.ReportLatency(white, TimeSpan.FromMilliseconds(50));
        session.ReportLatency(white, TimeSpan.FromMilliseconds(2000)); // one pathological sample

        // alpha=0.3: ema = 0.3*2000 + 0.7*50 = 635, far nearer 50 than 2000.
        Assert.Equal(635, session.CompensationMs(Side.White));
    }

    [Fact]
    public void Compensation_reaches_flag_fall()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 5;
        var (session, clock) = CreateSession(white, black, config);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromSeconds(5.2));

        // Raw elapsed (5.2s) alone would already flag White, but the 400ms credit brings the charged
        // interval to 4.8s -> floor 4, leaving 1s live. This is the test that would catch compensating
        // only ApplyMove and leaving CheckFlagFall/FlagFallMonitor uncompensated.
        Assert.False(session.CheckFlagFall());
        Assert.False(session.IsOver);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(session.CheckFlagFall());
        Assert.True(session.IsOver);
    }

    [Fact]
    public void Compensation_is_disabled_by_configuration()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        var lag = new LagCompensationOptions { LagCompensationEnabled = false };
        var (session, clock) = CreateSession(white, black, config, lag);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, session.CompensationMs(Side.White));

        clock.Advance(TimeSpan.FromSeconds(10));
        session.ApplyMove(white, Move.Parse("e2e4"));

        // Identical to No_measurement_means_no_compensation: the master switch reproduces exactly
        // pre-6f.2 behaviour even with a measurement on file.
        Assert.Equal(170, session.Position.WhiteTimeSeconds);
    }

    [Fact]
    public void LagMs_is_recorded_on_the_move_and_ThinkMs_stays_raw()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, clock) = CreateSession(white, black);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromSeconds(3));
        session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(3000, session.Moves[0].ThinkMs); // raw, receipt-to-receipt, unchanged meaning
        Assert.Equal(400, session.Moves[0].LagMs);     // what this ply was credited
    }

    [Fact]
    public void ReportLatency_ignores_a_player_who_is_not_in_the_game()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.ReportLatency(Guid.NewGuid(), TimeSpan.FromMilliseconds(400));

        Assert.Equal(0, session.CompensationMs(Side.White));
        Assert.Equal(0, session.CompensationMs(Side.Black));
    }

    [Fact]
    public void Solo_game_credits_both_sides_from_the_one_connection()
    {
        var soloId = Guid.NewGuid();
        var (session, _) = CreateSession(soloId, soloId);

        session.ReportLatency(soloId, TimeSpan.FromMilliseconds(200));

        // A solo/practice game has the same id on both sides, so the one connection compensates both -
        // otherwise Black would sit permanently uncompensated in a game the same person plays both ends of.
        Assert.True(session.CompensationMs(Side.White) > 0);
        Assert.True(session.CompensationMs(Side.Black) > 0);
    }

    [Fact]
    public void Old_shape_json_without_lag_ms_deserializes_with_default()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        // A 6e.2-era row: has ThinkMs/ReceivedAtUtc but predates LagMs entirely.
        const string oldShapeJson =
            """{"Ply":1,"Uci":"e2e4","Mover":"White","WhiteTimeAfter":170,"BlackTimeAfter":180,"WhiteDelta":-10,"BlackDelta":0,"Status":"Ongoing","ThinkMs":2500,"ReceivedAtUtc":"2026-01-01T12:00:00+00:00"}""";

        var record = JsonSerializer.Deserialize<MoveRecord>(oldShapeJson, options);

        Assert.NotNull(record);
        Assert.Equal(2500, record!.ThinkMs);
        Assert.Equal(0, record.LagMs);
    }

    [Fact]
    public void Compensation_does_not_change_the_economy_deltas()
    {
        // Regression guard mirroring 6e.2's own: the engine must be entirely unaware 6f.2 happened.
        // A config with no economy events means WhiteDelta/BlackDelta are zero whether or not a
        // latency was reported for the mover.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        session.ReportLatency(white, TimeSpan.FromMilliseconds(400));
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(0, result.WhiteTimeDelta);
        Assert.Equal(0, result.BlackTimeDelta);

        var record = session.Moves[0];
        Assert.Equal(0, record.WhiteDelta);
        Assert.Equal(0, record.BlackDelta);
    }
}
