using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// Write-behind persister for live games: after each move the hub/endpoint enqueues the game id,
/// and this service appends the new plies as <see cref="GameMove"/> rows off the hot path (a move
/// never waits on Postgres). Enqueues coalesce per game; the plies are read from the live session
/// at write time, so a coalesced write inserts every ply since the last successful one and can
/// never regress the log. On graceful shutdown every still-live session is flushed, so a deploy
/// loses nothing; a hard crash loses at most the plies not yet inserted. The finished
/// <see cref="Game.Moves"/> list belongs exclusively to GameRegistry.PersistIfOverAsync.
///
/// Phase 9c.7: the consumer drains up to <see cref="SnapshotPersistenceOptions.SnapshotBatchSize"/>
/// games and writes them in a single round trip. 9b.3 measured the pre-batch ceiling at 1/(write
/// RTT) independent of core count, 275-400 writes per second locally and an estimated 67-200 on
/// Neon, past which the unbounded channel silently accumulated durability debt (at 1600 games,
/// ~1060 of them permanently held an unwritten snapshot, with zero errors reported). Batching is
/// deliberately preferred to parallel consumers: it lifts the ceiling by the batch size rather than
/// by the worker count, and it keeps the single sequential consumer, so two writes for the same
/// game can never be in flight at once and an out-of-order write can never regress a move list.
/// </summary>
public sealed class LiveGamePersister : BackgroundService
{
    private readonly GameRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LiveGamePersister> _logger;
    private readonly KiriChessMetrics _metrics;
    private readonly int _batchSize;
    private readonly Channel<Guid> _work = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    /// <summary>Highest ply successfully inserted for a game in this process. 0 means nothing yet.</summary>
    private readonly ConcurrentDictionary<Guid, int> _persistedThrough = new();

    public LiveGamePersister(
        GameRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<LiveGamePersister> logger,
        KiriChessMetrics metrics,
        IOptions<SnapshotPersistenceOptions> options)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _batchSize = Math.Max(1, options.Value.SnapshotBatchSize);
    }

    /// <summary>
    /// Recovery has already rebuilt this game from durable plies. Further snapshots insert only
    /// plies above <paramref name="throughPly"/>.
    /// </summary>
    public void NotePersisted(Guid gameId, int throughPly) => _persistedThrough[gameId] = throughPly;

    /// <summary>Schedules a snapshot write for the game (coalesced, one pending write per game).</summary>
    public void EnqueueSnapshot(GameSession session)
    {
        if (session.IsOver) return; // the final write path owns finished games
        if (_queued.TryAdd(session.Id, 0))
            _work.Writer.TryWrite(session.Id);
    }

    /// <summary>Games with a coalesced snapshot write still pending, for the 6d.3 backlog gauge.</summary>
    public int PendingSnapshotCount => _queued.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new HashSet<Guid>(_batchSize);
        try
        {
            await foreach (var id in _work.Reader.ReadAllAsync(stoppingToken))
            {
                _queued.TryRemove(id, out _);
                batch.Add(id);

                // Opportunistic drain: TryRead never waits, so a server that is keeping up still
                // writes one game per round trip exactly as it did before 9c.7, with no added
                // latency. The batch only grows when work is already queued behind this one.
                while (batch.Count < _batchSize && _work.Reader.TryRead(out var queuedId))
                {
                    _queued.TryRemove(queuedId, out _);
                    // A game re-enqueued mid-drain dedupes into the set it is already in: the write
                    // below reads the live session, so one write covers both enqueues anyway.
                    batch.Add(queuedId);
                }

                await WriteBatchAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, StopAsync flushes whatever is still live.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Graceful shutdown (deploy): flush every still-live session so no move is lost. Batched for
        // the same reason as the steady-state path and with more urgency: flushing 1600 games one
        // round trip at a time at a 15ms Neon RTT would need ~24s, far past any shutdown grace, and
        // the flush would be killed part-way through with no sign of which games it had reached.
        //
        // I-6-8 / C-5-8: this used to leave no record of its own duration or session count, even when
        // it succeeded - 10a.3's shutdown-budget number was a timestamp-diff by eye against an
        // unrelated log line, because metrics stop being scraped the moment the process exits, so a
        // Meter instrument here would not be evidence either. The Stopwatch plus the two log lines
        // below are the whole fix: "grep Shutdown flush" on the deploy logs is now 10a.3's number, and
        // a shutdown token firing mid-flush (OperationCanceledException, which WriteBatchAsync's own
        // catch filter deliberately does not swallow) is logged with how many sessions never made it
        // out, rather than silently escaping StopAsync unnamed.
        var started = Stopwatch.GetTimestamp();
        var sessions = _registry.ActiveSessions;
        // Excludes sessions already over: the loop below skips them too (the final write path owns
        // finished games), so counting them here would inflate both the starting total and, on a
        // cancelled flush, the "never written" count with games that were never going to be flushed.
        var toFlush = sessions.Count(s => !s.IsOver);
        _logger.LogInformation("Shutdown flush starting: {Sessions} live sessions to flush", toFlush);

        var batch = new HashSet<Guid>(_batchSize);
        var flushed = 0;
        var batches = 0;
        try
        {
            foreach (var session in sessions)
            {
                if (session.IsOver) continue;
                batch.Add(session.Id);
                if (batch.Count < _batchSize) continue;
                await WriteBatchAsync(batch, cancellationToken);
                batches++;
                flushed += batch.Count;
                batch.Clear();
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch, cancellationToken);
                batches++;
                flushed += batch.Count;
            }

            _logger.LogInformation(
                "Shutdown flush complete: {Sessions} sessions, {Batches} batches, {ElapsedMs} ms",
                flushed, batches, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // The shutdown token itself fired mid-flush (kill_timeout exceeded before every batch
            // went out). Everything WriteBatchAsync already wrote is durable; `toFlush - flushed`
            // never made it, and this is the only place that would ever have said so.
            _logger.LogWarning(
                "Shutdown flush cancelled after {ElapsedMs} ms: {Flushed}/{Total} sessions flushed, {Unflushed} never written",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, flushed, toFlush, toFlush - flushed);
            throw;
        }
    }

    private async Task WriteBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var pending = new List<(Guid Id, MoveRecord[] Moves)>(ids.Count);
        try
        {
            // Plies are read here, at write time, never at enqueue time: that is what makes a
            // coalesced write unable to regress the log, and batching does not change it.
            //
            // This loop is deliberately inside the try. It used to sit above it, where anything it
            // threw escaped ExecuteAsync entirely: that is not the OperationCanceledException the
            // await-foreach catches, BackgroundServiceExceptionBehavior is left at its StopHost
            // default, and the whole host would go down with every live game on it. GameSession.Moves
            // now hands out a copy taken under the session's own lock, so the torn-read that used to
            // throw here cannot happen any more, but this service must never be one unexpected
            // exception away from stopping the process either way.
            foreach (var id in ids)
            {
                var session = _registry.Get(id);
                if (session is null || session.IsOver) continue; // the final write path owns finished games
                var tail = session.MovesAfter(_persistedThrough.GetOrAdd(id, 0));
                if (tail.Length == 0) continue;
                pending.Add((id, tail));
            }

            if (pending.Count == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var watermarks = new Dictionary<Guid, int>(pending.Count);
            foreach (var (id, moves) in pending)
            {
                var watermark = _persistedThrough.GetOrAdd(id, 0);
                foreach (var move in moves)
                {
                    if (move.Ply <= watermark) continue;
                    db.GameMoves.Add(new GameMove { GameId = id, Ply = move.Ply, Move = move });
                    if (!watermarks.TryGetValue(id, out var high) || move.Ply > high)
                        watermarks[id] = move.Ply;
                }
            }

            if (watermarks.Count == 0) return;

            await db.SaveChangesAsync(ct);
            foreach (var (id, ply) in watermarks)
                _persistedThrough[id] = ply;
            _metrics.SnapshotBatchWritten(watermarks.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The batch is one transaction, so a single bad id fails all of it, and a row that has
            // gone missing under a live session is exactly that case (EF reports the zero affected
            // rows as a concurrency failure). Retry one game at a time rather than drop the other
            // 63 good writes on the floor for one of them.
            _logger.LogWarning(
                ex, "Batched move-snapshot write of {Count} games failed; retrying individually", pending.Count);
            foreach (var (id, moves) in pending)
                await WriteOneAsync(id, moves, ct);
        }
    }

    private async Task WriteOneAsync(Guid id, IReadOnlyList<MoveRecord> moves, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            if (!await db.Games.AnyAsync(g => g.Id == id, ct)) return; // nothing to write to, not a failure
            var watermark = _persistedThrough.GetOrAdd(id, 0);
            var high = watermark;
            foreach (var move in moves)
            {
                if (move.Ply <= watermark) continue;
                db.GameMoves.Add(new GameMove { GameId = id, Ply = move.Ply, Move = move });
                if (move.Ply > high) high = move.Ply;
            }
            if (high == watermark) return;
            await db.SaveChangesAsync(ct);
            _persistedThrough[id] = high;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lost snapshot only widens the crash-loss window by one move; never take the game down.
            _logger.LogError(ex, "Move snapshot write failed for game {GameId}", id);
            _metrics.SnapshotFailed();
        }
    }
}
