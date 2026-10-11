using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Games;

/// <summary>
/// Persists a per-seat count of grace-timer arms (S-2-6). A drop that returns inside the window
/// never terminates the game, so <see cref="AbandonmentPolicy"/> never sees it and a moderator
/// looking at a report had nothing to read. This count is that record. Nothing in matchmaking,
/// ratings, or sanctions reads it.
/// </summary>
public interface IGraceDisconnectStore
{
    /// <summary>
    /// Schedules an increment for <paramref name="userId"/>'s seat in <paramref name="gameId"/>.
    /// Returns immediately. A database failure is logged and does not affect the grace timer.
    /// </summary>
    void RecordArmed(Guid gameId, Guid userId);
}

public sealed class GraceDisconnectStore : IGraceDisconnectStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GraceDisconnectStore> _logger;

    public GraceDisconnectStore(IServiceScopeFactory scopeFactory, ILogger<GraceDisconnectStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void RecordArmed(Guid gameId, Guid userId) => _ = RecordAsync(gameId, userId);

    /// <summary>
    /// The write <see cref="RecordArmed"/> fires in the background. Tests await this directly.
    /// Increments white's column when <paramref name="userId"/> sits white, including a solo game
    /// where both seats are the same account, and black's column otherwise. One statement, so two
    /// overlapping drops cannot lose an increment to a read-modify-write.
    /// </summary>
    public async Task RecordAsync(Guid gameId, Guid userId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

            var white = await db.Games
                .Where(g => g.Id == gameId && g.WhitePlayerId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    g => g.WhiteDisconnectCount, g => g.WhiteDisconnectCount + 1));
            if (white > 0) return;

            // WhitePlayerId != userId keeps a solo game (both seats, one account) from being
            // counted twice if the white update above ever missed.
            var black = await db.Games
                .Where(g => g.Id == gameId && g.BlackPlayerId == userId && g.WhitePlayerId != userId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    g => g.BlackDisconnectCount, g => g.BlackDisconnectCount + 1));
            if (black == 0)
            {
                _logger.LogWarning(
                    "Grace disconnect for user {UserId} in game {GameId} matched no seat.",
                    userId, gameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record a grace disconnect for user {UserId} in game {GameId}.",
                userId, gameId);
        }
    }
}
