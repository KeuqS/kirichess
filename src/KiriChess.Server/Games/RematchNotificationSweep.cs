using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KiriChess.Server.Games;

/// <summary>
/// Flushes rematch-offer mails that came due while no in-process timer was alive (P-3-7): a restart
/// inside the delay window, or a second instance that never armed. Separate from
/// <see cref="RematchNotificationSweeper"/> (the timer) for the reason every hosted service in this
/// codebase splits that way: the timer is untestable and the work is not.
///
/// Idempotent against the in-process delay: <see cref="RematchNotificationService.TrySendAsync"/>
/// claims <c>NotificationSentAt</c> with an <c>ExecuteUpdateAsync</c> that only one caller wins.
/// </summary>
public sealed class RematchNotificationSweep
{
    private readonly KiriChessDbContext _db;
    private readonly RematchNotificationService _notifications;
    private readonly IConfiguration _configuration;

    public RematchNotificationSweep(
        KiriChessDbContext db, RematchNotificationService notifications, IConfiguration configuration)
    {
        _db = db;
        _notifications = notifications;
        _configuration = configuration;
    }

    /// <summary>
    /// Sends every owed, still-unaccepted offer whose <c>NotificationDueAt</c> is in the past.
    /// No-ops when the notifier is disabled so a sweep tick cannot resurrect a send the operator
    /// turned off.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var delaySeconds = _configuration.GetValue("Rematch:NotifyDelaySeconds", 90);
        if (delaySeconds <= 0) return;

        var now = DateTimeOffset.UtcNow;
        var owed = await _db.RematchOffers.AsNoTracking()
            .Where(r => r.NotificationDueAt != null
                && r.NotificationDueAt <= now
                && r.NotificationSentAt == null
                && r.AcceptedAt == null)
            .Select(r => new { r.GameId, r.OfferedById })
            .ToListAsync(ct);

        foreach (var row in owed)
        {
            var game = await _db.Games.AsNoTracking()
                .Where(g => g.Id == row.GameId)
                .Select(g => new { g.WhitePlayerId, g.BlackPlayerId })
                .FirstOrDefaultAsync(ct);
            if (game is null)
            {
                await RematchNotificationService.ClearDueAtIfUnsentAsync(_db, row.GameId, ct);
                continue;
            }

            var opponentId = row.OfferedById == game.WhitePlayerId ? game.BlackPlayerId : game.WhitePlayerId;
            await _notifications.TrySendAsync(row.GameId, opponentId, ct);
        }
    }
}
