using System.Diagnostics.Metrics;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// The server's own metric instruments (Phase 6d.3). Instrumented with the platform
/// <see cref="Meter"/> API rather than a vendor SDK, so the exporter is a deployment choice and not
/// a code dependency, OpenTelemetry scrapes these in production, `dotnet-counters` reads the same
/// instruments locally with nothing installed.
///
/// Deliberately has no constructor dependencies: game services inject *this*, so anything that
/// needs to read game state to produce a value (the observable gauges) lives in
/// <see cref="GameMetricsObserver"/> instead, which would otherwise close a DI cycle.
///
/// Tag cardinality is bounded on purpose. `pool` is one of the six official pools or the literal
/// "custom"; nothing is ever tagged with a user id, game id, or anything else unbounded, a
/// per-game label would give the scraper a new time series for every game ever played.
/// </summary>
public sealed class KiriChessMetrics
{
    /// <summary>Meter name, also the string registered with the exporter in <c>Program.cs</c>.</summary>
    public const string MeterName = "KiriChess.Server";

    /// <summary>Tag value for games with no official rating pool (custom configs and bot games).</summary>
    public const string CustomPool = "custom";

    private readonly Counter<long> _gamesStarted;
    private readonly Counter<long> _gamesEnded;
    private readonly Counter<long> _movesApplied;
    private readonly Counter<long> _flagFallsDetected;
    private readonly Counter<long> _snapshotFailures;
    private readonly Counter<long> _snapshotWrites;
    private readonly Histogram<double> _snapshotBatchDuration;
    private readonly Counter<long> _hubErrors;
    private readonly Counter<long> _rateLimitRejections;
    private readonly Histogram<double> _moveApplyDuration;
    private readonly Histogram<double> _flagFallSweepDuration;
    private readonly Counter<long> _gameCommandsForwarded;
    private readonly Counter<long> _gameOwnershipTakeovers;
    private readonly Counter<long> _retentionRowsDeleted;
    private readonly Counter<long> _replayTruncated;

    public KiriChessMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _gamesStarted = meter.CreateCounter<long>(
            "kirichess.games.started", unit: "{game}",
            description: "Games created, by pool and rated flag.");
        _gamesEnded = meter.CreateCounter<long>(
            "kirichess.games.ended", unit: "{game}",
            description: "Games persisted as finished, by pool and termination reason.");
        _movesApplied = meter.CreateCounter<long>(
            "kirichess.moves.applied", unit: "{move}",
            description: "Legal moves accepted by the server, by pool.");
        _flagFallsDetected = meter.CreateCounter<long>(
            "kirichess.flagfall.detected", unit: "{game}",
            description: "Games ended by the idle flag-fall sweep rather than by a move.");
        _snapshotFailures = meter.CreateCounter<long>(
            "kirichess.persistence.snapshot_failures", unit: "{write}",
            description: "Write-behind move-snapshot writes that threw. Non-zero means durability is degraded.");
        _snapshotWrites = meter.CreateCounter<long>(
            "kirichess.persistence.snapshot_writes", unit: "{game}",
            description: "Games whose move snapshot was written. Divide by the count of snapshot_batch.duration to get games per round trip: a value near 1 means the persister is keeping up, a value near the batch size means it is only keeping up because it is batching.");
        _snapshotBatchDuration = meter.CreateHistogram<double>(
            "kirichess.persistence.snapshot_batch.duration", unit: "ms",
            description: "Wall time of one batched snapshot round trip. This is the write RTT that sets the persister's ceiling; 9b.3 found the ceiling is 1/(this) per game before batching.");
        _hubErrors = meter.CreateCounter<long>(
            "kirichess.hub.errors", unit: "{error}",
            description: "Unexpected exceptions thrown out of a SignalR hub method, by method. Excludes HubException, which is normal control flow.");
        _rateLimitRejections = meter.CreateCounter<long>(
            "kirichess.ratelimit.rejected", unit: "{request}",
            description: "Requests refused by a rate limiter, by surface (http/hub) and policy. A steady non-zero rate on the move policies means the limits are too tight, not that abuse is happening.");

        _moveApplyDuration = meter.CreateHistogram<double>(
            "kirichess.move.apply.duration", unit: "ms",
            description: "Server-side time to validate, apply and broadcast one move.");
        _flagFallSweepDuration = meter.CreateHistogram<double>(
            "kirichess.flagfall.sweep.duration", unit: "ms",
            description: "Wall time of one flag-fall sweep across all live sessions. The sweep runs once a second; approaching 1000ms means the safety net is falling behind.");

        // --- Multi-instance game ownership (Phase 8.4) ---
        _gameCommandsForwarded = meter.CreateCounter<long>(
            "kirichess.game.command.forwarded", unit: "{command}",
            description: "Player actions forwarded to another instance because it owns the game, by command kind and outcome (ok/rejected/timeout/error). Zero forever means Redis is disabled or nothing has ever needed forwarding; a steady non-ok rate means forwarding itself is unhealthy, not that players are misbehaving.");
        _gameOwnershipTakeovers = meter.CreateCounter<long>(
            "kirichess.game.ownership.takeovers", unit: "{game}",
            description: "Games this instance took over because their previous owner's lease had expired. A steady non-zero rate outside of deploys means instances are dying unexpectedly, not routine ownership churn.");

        // --- Data retention (Phase 8a.3) ---
        _retentionRowsDeleted = meter.CreateCounter<long>(
            "kirichess.retention.rows_deleted", unit: "{row}",
            description: "Rows removed by the data-retention sweep, by rule.");

        // --- Replay/PGN fidelity (C-8-3) ---
        _replayTruncated = meter.CreateCounter<long>(
            "kirichess.replay.truncated", unit: "{game}",
            description: "A game whose stored move log stopped replaying part way through, by artifact (replay/pgn). GameReplayBuilder and PgnBuilder both serve the reconstructable prefix rather than 500ing a public page, which is right, but that used to leave no log line, no metric and no field on the response - a corrupt row was invisible to the maintainer forever. Non-zero here means a persisted game cannot be fully reconstructed.");
    }

    /// <summary>Normalises a session's rating pool into a bounded tag value.</summary>
    public static string PoolTag(string? ratingPool) =>
        string.IsNullOrEmpty(ratingPool) ? CustomPool : ratingPool;

    public void GameStarted(string? pool, bool rated) =>
        _gamesStarted.Add(1, new KeyValuePair<string, object?>("pool", PoolTag(pool)),
                             new KeyValuePair<string, object?>("rated", rated));

    public void GameEnded(string? pool, bool rated, string termination) =>
        _gamesEnded.Add(1, new KeyValuePair<string, object?>("pool", PoolTag(pool)),
                           new KeyValuePair<string, object?>("rated", rated),
                           new KeyValuePair<string, object?>("termination", termination));

    public void MoveApplied(string? pool, double elapsedMs)
    {
        var tag = new KeyValuePair<string, object?>("pool", PoolTag(pool));
        _movesApplied.Add(1, tag);
        _moveApplyDuration.Record(elapsedMs, tag);
    }

    public void FlagFallDetected() => _flagFallsDetected.Add(1);

    public void FlagFallSweepCompleted(double elapsedMs) => _flagFallSweepDuration.Record(elapsedMs);

    public void SnapshotFailed() => _snapshotFailures.Add(1);

    /// <summary>One batched snapshot round trip completed (Phase 9c.7).</summary>
    public void SnapshotBatchWritten(int games, double elapsedMs)
    {
        _snapshotWrites.Add(games);
        _snapshotBatchDuration.Record(elapsedMs);
    }

    public void HubError(string method) =>
        _hubErrors.Add(1, new KeyValuePair<string, object?>("method", method));

    /// <summary>
    /// <paramref name="policy"/> is always one of the fixed policy-name constants
    /// (<see cref="RateLimitPolicies"/>), a hub method-class tag, or the literal "global", never a
    /// user id, IP or connection id, which would be the unbounded cardinality this file's header
    /// warns about.
    /// </summary>
    public void RateLimitRejected(string surface, string policy) =>
        _rateLimitRejections.Add(1, new KeyValuePair<string, object?>("surface", surface),
                                    new KeyValuePair<string, object?>("policy", policy));

    /// <summary><paramref name="outcome"/> is always one of "ok", "rejected", "timeout" or "error", never anything unbounded.</summary>
    public void GameCommandForwarded(string kind, string outcome) =>
        _gameCommandsForwarded.Add(1, new KeyValuePair<string, object?>("kind", kind),
                                      new KeyValuePair<string, object?>("outcome", outcome));

    public void GameOwnershipTakeover() => _gameOwnershipTakeovers.Add(1);

    /// <summary>
    /// Phase 8a.3. `rule` is one of RetentionRules - a bounded set of two, which is the cardinality
    /// discipline this class's header comment demands. Never tagged with a user id or a table row.
    /// </summary>
    public void RetentionRowsDeleted(string rule, int rows) =>
        _retentionRowsDeleted.Add(rows, new KeyValuePair<string, object?>("rule", rule));

    /// <summary>
    /// C-8-3. <paramref name="artifact"/> is always "replay" or "pgn", the two callers
    /// (<c>Games.GameReplayBuilder</c> and <c>Games.PgnBuilder</c>), never anything unbounded like a
    /// game id.
    /// </summary>
    public void ReplayTruncated(string artifact) =>
        _replayTruncated.Add(1, new KeyValuePair<string, object?>("artifact", artifact));
}
