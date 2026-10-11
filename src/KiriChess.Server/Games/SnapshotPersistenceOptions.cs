namespace KiriChess.Server.Games;

/// <summary>
/// Write-behind snapshot policy (Phase 9c.7). 9b.3 measured <see cref="LiveGamePersister"/>'s
/// ceiling at 1/(write RTT): one sequential consumer, one round trip per game, so 275-400 snapshot
/// writes per second locally and an estimated 67-200 on Neon. Past that the unbounded channel
/// absorbs the shortfall silently as durability debt, and 6c.2's "a hard crash loses at most the
/// tail move" quietly stops being true for most live games at once. Writing several games per round
/// trip multiplies that ceiling by the batch size without touching the single-consumer ordering the
/// per-game coalescing depends on.
/// </summary>
public sealed class SnapshotPersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>
    /// Most games written in one round trip. The drain is opportunistic and never waits for a batch
    /// to fill, so below saturation a batch is a single game and the behaviour is exactly what it
    /// was before 9c.7; batches only grow once writes are already falling behind, which is the case
    /// this exists for. 64 sits well inside Npgsql's command batching and bounds what one in-flight
    /// batch holds in memory (a ply-200 move list is a few tens of KB).
    /// </summary>
    public int SnapshotBatchSize { get; set; } = 64;
}
