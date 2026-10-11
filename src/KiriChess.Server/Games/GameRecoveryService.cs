using KiriChess.Engine;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games.Distributed;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// Boot-time recovery: reloads every Ongoing game into the registry by replaying its move log,
/// then gives players a resume window to rejoin. Games nobody resumes are aborted, no winner, no
/// rating change (Termination = ServerRestart), because a restart is the server's fault and must
/// never hand out a win or silently vanish a game. Recovery failure is logged, never fatal: the
/// server prefers booting without recovered games over not booting (same availability stance as
/// /health tolerating a dead DB).
///
/// Phase 8.4: every instance in a cluster runs this against the same Ongoing rows at boot. The
/// ownership-lease claim below is what makes only one instance actually take each game, a rolling
/// deploy is safe because the instance still running an old process keeps its games (its lease is
/// unaffected by a sibling booting) until it exits and its lease lapses, at which point the takeover
/// sweep or the next touch picks them up (see Phase 8.4 Step 7).
/// </summary>
public sealed class GameRecoveryService
{
    private readonly KiriChessEngine _engine;
    private readonly GameRegistry _registry;
    private readonly GameConnectionTracker _connections;
    private readonly GameOwnership _ownership;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<GameHub> _hub;
    private readonly IConfiguration _config;
    private readonly LagCompensationOptions _lagOptions;
    private readonly LiveGamePersister _persister;
    private readonly ILogger<GameRecoveryService> _logger;

    public GameRecoveryService(
        KiriChessEngine engine,
        GameRegistry registry,
        GameConnectionTracker connections,
        GameOwnership ownership,
        IServiceScopeFactory scopeFactory,
        IHubContext<GameHub> hub,
        IConfiguration config,
        IOptions<LagCompensationOptions> lagOptions,
        LiveGamePersister persister,
        ILogger<GameRecoveryService> logger)
    {
        _engine = engine;
        _registry = registry;
        _connections = connections;
        _ownership = ownership;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _config = config;
        _lagOptions = lagOptions.Value;
        _persister = persister;
        _logger = logger;
    }

    /// <summary>Restores Ongoing rows into the registry and arms the resume-window sweep.</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        List<Guid> restored = new();
        var skippedAlreadyOwned = 0;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var ongoing = await db.Games.Where(g => g.Result == GameResult.Ongoing).ToListAsync(ct);
            if (ongoing.Count == 0) return;

            var ids = ongoing.Select(g => g.Id).ToList();
            var appended = await db.GameMoves.Where(m => ids.Contains(m.GameId)).ToListAsync(ct);
            var appendedByGame = appended.GroupBy(m => m.GameId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<MoveRecord>)g.Select(m => m.Move).ToList());

            foreach (var row in ongoing)
            {
                var merged = GameMoveLog.Merge(
                    row.Moves, appendedByGame.GetValueOrDefault(row.Id) ?? []);
                // Replay a copy. Assigning the merged list onto the tracked row would make the
                // SaveChanges below rewrite Game.Moves, which is the full-list write this path avoids.
                var replay = new Game
                {
                    Id = row.Id,
                    WhitePlayerId = row.WhitePlayerId,
                    BlackPlayerId = row.BlackPlayerId,
                    RatingPool = row.RatingPool,
                    IsRated = row.IsRated,
                    WhiteViaApi = row.WhiteViaApi,
                    BlackViaApi = row.BlackViaApi,
                    StartFen = row.StartFen,
                    ConfigSnapshot = row.ConfigSnapshot,
                    Moves = merged,
                    CreatedAt = row.CreatedAt
                };
                _persister.NotePersisted(row.Id, merged.Count == 0 ? 0 : merged.Max(m => m.Ply));

                GameSession session;
                try
                {
                    session = GameSession.Restore(_engine, replay, null, _lagOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Game {GameId} could not be replayed; aborting it", row.Id);
                    row.Result = GameResult.Aborted;
                    row.Termination = TerminationReason.ServerRestart;
                    row.EndedAt = DateTimeOffset.UtcNow;
                    continue;
                }

                // --- Multi-instance ownership (Phase 8.4) ---
                // Single instance: always succeeds (GameOwnership.TryClaimAsync's disabled path).
                // Multi-instance: only the first instance to claim each row keeps it, every sibling
                // booting against the same Ongoing rows skips one it lost the race for, which is
                // exactly what makes a rolling deploy safe.
                if (!await _ownership.TryClaimAsync(session.Id, ct))
                {
                    skippedAlreadyOwned++;
                    continue;
                }

                if (session.IsOver)
                {
                    // The crash landed between the last move snapshot and the final write,
                    // the replay itself finished the game. Complete it properly (ratings included).
                    _registry.Restore(session);
                    await _registry.PersistIfOverAsync(session);
                    continue;
                }

                _registry.Restore(session);
                restored.Add(session.Id);
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Recovered {Count} live game(s) after restart ({Skipped} already owned by another instance)",
                restored.Count, skippedAlreadyOwned);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live-game recovery failed; serving without recovered games");
        }

        if (restored.Count > 0)
            _ = SweepUnresumedAsync(restored); // fire-and-forget: the window outlives startup
    }

    private async Task SweepUnresumedAsync(IReadOnlyList<Guid> gameIds)
    {
        var graceSeconds = _config.GetValue("Games:ResumeGraceSeconds", 60);
        await Task.Delay(TimeSpan.FromSeconds(graceSeconds));

        foreach (var id in gameIds)
        {
            var session = _registry.Get(id);
            if (session is null || session.IsOver) continue;

            var required = session.WhitePlayerId == session.BlackPlayerId
                ? new[] { session.WhitePlayerId }
                : new[] { session.WhitePlayerId, session.BlackPlayerId };

            var everyoneConnected = true;
            foreach (var playerId in required)
            {
                if (!await _connections.IsConnectedAsync(id, playerId))
                {
                    everyoneConnected = false;
                    break;
                }
            }
            if (everyoneConnected) continue;

            try
            {
                session.Abort();
            }
            catch (InvalidGameActionException)
            {
                continue; // finished in the race window, the finishing path persisted it
            }

            await _registry.PersistIfOverAsync(session);
            await GameHubGroups.SendToRoomAsync(_hub.Clients, id, "GameEnded", new GameEndedDto(session.ToState()));
            _logger.LogInformation("Aborted unresumed game {GameId} after restart", id);
        }
    }
}
