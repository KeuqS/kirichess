using System.Collections.Concurrent;

namespace KiriChess.LoadTest;

/// <summary>
/// Shared, thread-safe aggregator every <see cref="BotPlayer"/> reports to. The harness does not
/// pre-assign partners (README § Decision 2) - whoever the server pairs via JoinQueue is who plays -
/// so there is no per-pair object to "drive alternation" through; each BotPlayer already knows whose
/// turn it is from the State.SideToMove the server broadcasts. What this class owns instead is the
/// one thing the client can measure that the server cannot: the invoke -> own-receipt move-latency
/// samples (README § Decision 4), plus the counters the § Decision 6 criteria and the generator-
/// honesty check need.
/// </summary>
internal sealed class GameCoordinator
{
    private readonly ConcurrentBag<double> _moveLatenciesMs = new();

    private long _movesSubmitted;
    private long _movesReceived;
    private long _matchesFound;
    private long _gamesEnded;
    private long _reconnectsOrCloses;
    private long _pingsAnswered;
    private readonly ConcurrentDictionary<string, long> _errorsByCategory = new();

    public void RecordMatchFound() => Interlocked.Increment(ref _matchesFound);
    public void RecordMoveSubmitted() => Interlocked.Increment(ref _movesSubmitted);
    public void RecordGameEnded() => Interlocked.Increment(ref _gamesEnded);
    public void RecordReconnectOrClose() => Interlocked.Increment(ref _reconnectsOrCloses);
    public void RecordPingAnswered() => Interlocked.Increment(ref _pingsAnswered);

    public void RecordMoveLatency(double ms)
    {
        Interlocked.Increment(ref _movesReceived);
        _moveLatenciesMs.Add(ms);
    }

    public void RecordError(string category) =>
        _errorsByCategory.AddOrUpdate(category, 1, (_, v) => v + 1);

    /// <summary>Clears the raw latency sample bag. Called once the ramp is complete and warm-up has
    /// drained, so the reported percentiles cover only the measurement window (task § 2.5 step 5).
    /// The counters below are NEVER reset by this class - Program.cs takes a <see cref="Snapshot"/>
    /// immediately after calling this and diffs the counters it wants window-scoped (moves submitted/
    /// received) against a second snapshot at window-close, exactly like the server-side counters are
    /// diffed. Pings-answered/reconnects/errors are reported as whole-run totals instead, since a
    /// reconnect during the ramp or warm-up is still a finding.</summary>
    public void ResetLatencySamples()
    {
        while (_moveLatenciesMs.TryTake(out _)) { }
    }

    public CoordinatorSnapshot Snapshot() => new(
        MovesSubmitted: Interlocked.Read(ref _movesSubmitted),
        MovesReceived: Interlocked.Read(ref _movesReceived),
        MatchesFound: Interlocked.Read(ref _matchesFound),
        GamesEnded: Interlocked.Read(ref _gamesEnded),
        ReconnectsOrCloses: Interlocked.Read(ref _reconnectsOrCloses),
        PingsAnswered: Interlocked.Read(ref _pingsAnswered),
        ErrorsByCategory: _errorsByCategory.ToDictionary(kv => kv.Key, kv => kv.Value),
        MoveLatenciesMs: _moveLatenciesMs.ToArray());
}

internal sealed record CoordinatorSnapshot(
    long MovesSubmitted,
    long MovesReceived,
    long MatchesFound,
    long GamesEnded,
    long ReconnectsOrCloses,
    long PingsAnswered,
    IReadOnlyDictionary<string, long> ErrorsByCategory,
    IReadOnlyList<double> MoveLatenciesMs);
