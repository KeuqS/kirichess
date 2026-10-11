using System.Diagnostics.Metrics;
using KiriChess.Server.Infrastructure;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Proves the instruments in <see cref="KiriChessMetrics"/> record what they claim, with no host at
/// all, a <see cref="MeterListener"/> observes the raw measurements, and <see cref="DummyMeterFactory"/>
/// stands in for the one dependency the class takes.
/// </summary>
public sealed class KiriChessMetricsTests
{
    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters) meter.Dispose();
        }

        /// <summary>True when this factory created <paramref name="meter"/>. See <see cref="Listen"/>.</summary>
        public bool Owns(Meter meter) => _meters.Contains(meter);
    }

    private sealed record Recorded(string InstrumentName, object? Value, KeyValuePair<string, object?>[] Tags);

    /// <summary>
    /// Wires a <see cref="MeterListener"/> onto the instruments of <paramref name="factory"/>'s own
    /// meter.
    ///
    /// Scoped to the factory instance, not just to <see cref="KiriChessMetrics.MeterName"/>. A
    /// MeterListener is process-wide, and every WebApplicationFactory-backed test running in parallel
    /// has its own KiriChessMetrics under the same meter name; matching on the name alone captured
    /// those too. That made these tests race twice over against any test class that happens to create
    /// a game at the same moment: a foreign measurement could arrive mid-assertion ("Collection was
    /// modified; enumeration operation may not execute") or simply be counted, breaking an
    /// Assert.Single that is asking about this test's one call. Filtering by the meter this test
    /// created is what makes them about this test again.
    /// </summary>
    private static (MeterListener Listener, List<Recorded> Measurements) Listen(DummyMeterFactory factory)
    {
        var measurements = new List<Recorded>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KiriChessMetrics.MeterName && factory.Owns(instrument.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Recorded(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Recorded(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return (listener, measurements);
    }

    [Fact]
    public void GameStarted_records_one_measurement_with_pool_and_rated_tags()
    {
        using var factory = new DummyMeterFactory();
        var metrics = new KiriChessMetrics(factory);
        var (listener, measurements) = Listen(factory);
        using var _ = listener;

        metrics.GameStarted("bounty1", true);

        var m = Assert.Single(measurements, m => m.InstrumentName == "kirichess.games.started");
        Assert.Contains(m.Tags, t => t.Key == "pool" && Equals(t.Value, "bounty1"));
        Assert.Contains(m.Tags, t => t.Key == "rated" && Equals(t.Value, true));
    }

    [Fact]
    public void MoveApplied_records_both_the_counter_and_the_histogram()
    {
        using var factory = new DummyMeterFactory();
        var metrics = new KiriChessMetrics(factory);
        var (listener, measurements) = Listen(factory);
        using var _ = listener;

        metrics.MoveApplied("custom", 4.2);

        Assert.Contains(measurements, m => m.InstrumentName == "kirichess.moves.applied");
        var histogram = Assert.Single(measurements, m => m.InstrumentName == "kirichess.move.apply.duration");
        Assert.Equal(4.2, (double)histogram.Value!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PoolTag_normalises_missing_pool_to_custom(string? pool)
    {
        Assert.Equal(KiriChessMetrics.CustomPool, KiriChessMetrics.PoolTag(pool));
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("pgn")]
    public void ReplayTruncated_records_one_measurement_with_the_artifact_tag(string artifact)
    {
        // C-8-3: GameReplayBuilder and PgnBuilder both call this on the truncation `break`; this
        // proves the instrument itself carries the right name and tag, independent of either caller.
        using var factory = new DummyMeterFactory();
        var metrics = new KiriChessMetrics(factory);
        var (listener, measurements) = Listen(factory);
        using var _ = listener;

        metrics.ReplayTruncated(artifact);

        var m = Assert.Single(measurements, m => m.InstrumentName == "kirichess.replay.truncated");
        Assert.Contains(m.Tags, t => t.Key == "artifact" && Equals(t.Value, artifact));
    }
}
