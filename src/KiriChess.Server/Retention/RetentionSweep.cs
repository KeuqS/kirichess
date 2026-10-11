using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Retention;

/// <summary>
/// Phase 8a.3. Executes the declared retention schedule once. Separate from RetentionSweeper (the
/// timer) for the reason every hosted service in this codebase splits that way: the timer is
/// untestable and the work is not, so the work takes an explicit `now` and returns a result a test
/// can assert on.
///
/// Deletes are one unbounded statement per rule. That is a deliberate limit and not an oversight:
/// bounding work per tick is what OrphanedGameSweeper does against a live index during an outage,
/// which is a different problem from steady-state row count. The cutoff columns are indexed
/// (C-12-2), so the cost of a large sweep is the delete itself, not finding the rows. When a
/// single DELETE starts to hurt, bound it with a Take on that same indexed predicate - not a
/// longer interval, which only makes each sweep bigger, and not a walk of the primary key,
/// which still has to find the expired rows.
///
/// Every instance runs this. ExecuteDeleteAsync over an already-empty set deletes zero rows, so N
/// instances sweeping is duplicated work and never a wrong answer; a Redis lock was considered and
/// rejected as machinery guarding nothing. Production is pinned to one machine anyway (DH.1).
/// </summary>
public sealed class RetentionSweep
{
    private readonly KiriChessDbContext _db;
    private readonly RetentionOptions _options;
    private readonly KiriChessMetrics _metrics;
    private readonly ILogger<RetentionSweep> _logger;

    public RetentionSweep(
        KiriChessDbContext db, IOptions<RetentionOptions> options,
        KiriChessMetrics metrics, ILogger<RetentionSweep> logger)
    {
        _db = db;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Runs every rule in the schedule against `now` and returns what each removed. `now` is a
    /// parameter, not UtcNow, so RetentionSweepTests can prove a boundary without sleeping.
    /// </summary>
    public async Task<RetentionSweepResult> RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var results = new List<RetentionRuleResult>();

        foreach (var rule in RetentionSchedule.From(_options))
        {
            var cutoff = RetentionSchedule.CutoffFor(rule, now);

            // The switch's default throws rather than skips - a rule added to the schedule with no
            // delete behind it must fail loudly at the first sweep, not silently do nothing forever.
            var deleted = rule.Name switch
            {
                RetentionRules.RefreshTokens =>
                    // ExpiresAt, never RevokedAt - README ground truth 2.
                    await _db.RefreshTokens.Where(t => t.ExpiresAt < cutoff).ExecuteDeleteAsync(ct),
                RetentionRules.ExternalLoginCodes =>
                    await _db.ExternalLoginCodes.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct),
                RetentionRules.TwoFactorChallenges =>
                    await _db.TwoFactorChallenges.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct),
                RetentionRules.Challenges =>
                    await _db.Challenges.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct),
                RetentionRules.ApiTokens =>
                    await _db.ApiTokens.Where(t =>
                        (t.ExpiresAt != null && t.ExpiresAt < cutoff)
                        || (t.RevokedAt != null && t.RevokedAt < cutoff)).ExecuteDeleteAsync(ct),
                // S-3-1. The one rule that is an UPDATE, not a DELETE: the row is a tombstone that
                // has to survive (Restrict foreign keys), so what expires is the marker column on it.
                // The count it contributes is rows CLEARED rather than rows deleted, which is the one
                // place the "rows_deleted" name in the log line and the metric reads loosely; adding
                // a second counter for one rule was judged worse than this sentence.
                //
                // The predicate is "no account ban on this row has been active any more recently
                // than the cutoff". A permanent, unlifted ban has LiftedAt and ExpiresAt both null,
                // so the inner Any matches and the marker is kept forever, which is correct - a
                // permanent ban has no end for the marker's life to be measured from.
                RetentionRules.BanEvasionMarkers =>
                    await _db.Users.Where(u =>
                        u.BanEvasionEmailHash != null
                        && !_db.UserSanctions.Any(s =>
                            s.UserId == u.Id
                            && s.Type == SanctionType.AccountBan
                            && (s.LiftedAt == null || s.LiftedAt > cutoff)
                            && (s.ExpiresAt == null || s.ExpiresAt > cutoff)))
                        .ExecuteUpdateAsync(
                            set => set.SetProperty(u => u.BanEvasionEmailHash, (string?)null), ct),
                _ => throw new InvalidOperationException($"Retention rule '{rule.Name}' has no delete."),
            };

            if (deleted > 0) _metrics.RetentionRowsDeleted(rule.Name, deleted);
            results.Add(new RetentionRuleResult(rule.Name, deleted));
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var result = new RetentionSweepResult(now, results);

        // One line, structured, counts only - never a user id, an email or a token hash.
        _logger.LogInformation(
            "Retention sweep removed {RowsDeleted} rows in {ElapsedMs}ms: {RuleCounts}",
            result.TotalRowsDeleted,
            (int)elapsed.TotalMilliseconds,
            string.Join(", ", results.Select(r => $"{r.Name}={r.RowsDeleted}")));

        return result;
    }
}
