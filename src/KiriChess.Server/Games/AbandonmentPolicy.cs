using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Games;

/// <summary>
/// Matchmaking eligibility for repeat abandoners (Phase 6e.3). Derived from the Games table rather
/// than an in-memory strike counter on purpose: a counter in the heap is wiped by exactly the deploy
/// an abuser can provoke by waiting, and a new table would duplicate a fact the game rows already
/// record. Not a rating penalty, the forfeited games already cost rating, and there is no appeal
/// path until moderation tooling exists (Phase 8.5).
///
/// A drop that returns inside the grace window is deliberately not an offence here. It is recorded
/// on the game (<see cref="GraceDisconnectStore"/>) for the moderator dossier, and this policy
/// does not read that count.
/// </summary>
public sealed class AbandonmentPolicy
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public AbandonmentPolicy(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    /// <summary>Whether <paramref name="userId"/> may enter a queue right now, and if not, for how long.</summary>
    public sealed record Verdict(bool Allowed, TimeSpan RetryAfter, int RecentAbandons);

    public async Task<Verdict> EvaluateAsync(Guid userId, CancellationToken ct = default)
    {
        var windowHours = _configuration.GetValue("Matchmaking:AbandonWindowHours", 24);
        var threshold = _configuration.GetValue("Matchmaking:AbandonThreshold", 3);
        var cooldownMinutes = _configuration.GetValue("Matchmaking:AbandonCooldownMinutes", 15);

        // Threshold 0 or cooldown 0 disables the policy entirely, always allowed. This is the
        // documented escape hatch (README/.env.example) for turning the cooldown off without a
        // redeploy of code, e.g. while tuning the threshold in production.
        if (threshold <= 0 || cooldownMinutes <= 0)
            return new Verdict(true, TimeSpan.Zero, 0);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(windowHours);
        var offences = await db.Games
            .Where(g => g.EndedAt != null && g.EndedAt > cutoff)
            // Abandoned losses, plus NoShow rows attributed to this user. ServerRestart stays
            // excluded: a restart is nobody's fault. A NoShow with a null at-fault id (both seats
            // missing) also charges nobody, because it does not match NoShowAtFaultPlayerId.
            //
            // C-6-2: never joining a matched game used to cost nothing while disconnecting after
            // joining counted toward the cooldown. The at-fault column closes that: three attributed
            // no-shows in the window trip the same cooldown as three abandons. The player who showed
            // up is not charged. Still not a rated win; Glicko is unchanged.
            .Where(g =>
                (g.Termination == TerminationReason.Abandoned
                 && ((g.WhitePlayerId == userId && g.Result == GameResult.BlackWins)
                     || (g.BlackPlayerId == userId && g.Result == GameResult.WhiteWins)))
                || (g.Termination == TerminationReason.NoShow && g.NoShowAtFaultPlayerId == userId))
            .OrderByDescending(g => g.EndedAt)
            .Select(g => g.EndedAt!.Value)
            .Take(threshold + 1)
            .ToListAsync(ct);

        if (offences.Count < threshold)
            return new Verdict(true, TimeSpan.Zero, offences.Count);

        var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
        var mostRecent = offences[0];
        var retryAfter = mostRecent + cooldown - DateTimeOffset.UtcNow;

        if (retryAfter <= TimeSpan.Zero)
            return new Verdict(true, TimeSpan.Zero, offences.Count);

        return new Verdict(false, retryAfter, offences.Count);
    }
}
