using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

/// <summary>
/// Builds the Phase 8a.2 GDPR export (Art. 20) for one account. Scoped: takes a DbContext and is only
/// ever called from a request handler. See README scope decision 4 (synchronous JSON download, no
/// email) and scope decision 5 (what must never appear in this file).
/// </summary>
public sealed class AccountExportBuilder
{
    // 3: each game carries the exporting player's own grace-disconnect count (S-2-6).
    public const string ExportVersion = "3";
    public const int GamesCap = 5000;
    public const int RatingHistoryCap = 10000;

    private readonly KiriChessDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public AccountExportBuilder(KiriChessDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    /// <summary>
    /// Null when the account does not exist or is already a tombstone - a deleted account has nothing
    /// left to export and must not 200 with a scrubbed profile (README scope decision 4/5 context).
    /// </summary>
    public async Task<AccountExportDto?> BuildAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.DeletedAt is not null) return null;

        // --- Games: take one more than the cap so truncation is known without a second COUNT(*). ---
        var gameRows = await _db.Games
            .Where(g => g.WhitePlayerId == userId || g.BlackPlayerId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .Take(GamesCap + 1)
            .Select(g => new
            {
                g.Id,
                g.WhitePlayerId,
                g.BlackPlayerId,
                g.Result,
                g.Termination,
                g.RatingPool,
                g.IsRated,
                g.PlyCount,
                g.StartFen,
                g.Moves,
                g.CreatedAt,
                g.EndedAt,
                g.WhiteDisconnectCount,
                g.BlackDisconnectCount
            })
            .ToListAsync(ct);

        var gamesTruncated = gameRows.Count > GamesCap;
        if (gamesTruncated) gameRows.RemoveRange(GamesCap, gameRows.Count - GamesCap);

        // Batched opponent-name resolution: one query for every distinct opponent id in the page,
        // never one query per game. Falls back to "Unknown" for a missing row, matching
        // GameNameResolver's own fallback for a row it cannot resolve.
        var opponentIds = gameRows
            .Select(g => g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId)
            .Distinct()
            .ToList();
        var opponentNames = await _db.Users
            .Where(u => opponentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var games = gameRows.Select(g =>
        {
            var isWhite = g.WhitePlayerId == userId;
            var opponentId = isWhite ? g.BlackPlayerId : g.WhitePlayerId;
            // Solo games increment only the white column. isWhite is true for that account.
            var disconnects = isWhite ? g.WhiteDisconnectCount : g.BlackDisconnectCount;
            return new AccountExportGameDto(
                g.Id,
                isWhite ? "white" : "black",
                opponentNames.GetValueOrDefault(opponentId, "Unknown"),
                g.Result.ToString(),
                g.Termination.ToString(),
                g.RatingPool,
                g.IsRated,
                g.PlyCount,
                g.StartFen,
                g.Moves.Select(m => m.Uci).ToArray(),
                g.CreatedAt,
                g.EndedAt,
                disconnects);
        }).ToArray();

        // --- Rating history: same cap-plus-one pattern. ---
        var historyRows = await _db.RatingHistories
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(RatingHistoryCap + 1)
            .Select(r => new AccountExportRatingHistoryDto(r.Pool, r.GameId, r.ValueAfter, r.RdAfter, r.CreatedAt))
            .ToListAsync(ct);

        var ratingHistoryTruncated = historyRows.Count > RatingHistoryCap;
        if (ratingHistoryTruncated) historyRows.RemoveRange(RatingHistoryCap, historyRows.Count - RatingHistoryCap);

        var ratings = await _db.Ratings
            .Where(r => r.UserId == userId)
            .Select(r => new AccountExportRatingDto(r.Pool, r.Value, r.RatingDeviation, r.Volatility, r.GamesPlayed, r.UpdatedAt))
            .ToArrayAsync(ct);

        var reportsFiled = await _db.Reports
            .Where(r => r.ReporterId == userId)
            .Select(r => new AccountExportReportDto(
                r.Id, r.GameId, r.Reason.ToString(), r.Details, r.Status.ToString(), r.CreatedAt, r.ReviewedAt))
            .ToArrayAsync(ct);

        // Deliberately no ReporterId/name here - see README scope decision 5 and the DTO's own summary.
        var reportsAgainst = await _db.Reports
            .Where(r => r.ReportedUserId == userId)
            .Select(r => new AccountExportReportAgainstDto(r.Id, r.GameId, r.Reason.ToString(), r.Status.ToString(), r.CreatedAt))
            .ToArrayAsync(ct);

        // ModeratorNote and IssuedByUserId are never projected - same withholding SanctionDto already
        // does on the 8.5 player-facing surface.
        var sanctions = await _db.UserSanctions
            .Where(s => s.UserId == userId)
            .Select(s => new AccountExportSanctionDto(s.Id, s.Type.ToString(), s.Reason, s.IssuedAt, s.ExpiresAt, s.LiftedAt))
            .ToArrayAsync(ct);

        // TokenHash is projected OUT here, not merely omitted from the DTO afterwards - it never even
        // loads into memory.
        var sessions = await _db.RefreshTokens
            .Where(t => t.UserId == userId)
            .Select(t => new AccountExportSessionDto(t.CreatedAt, t.ExpiresAt, t.RevokedAt))
            .ToArrayAsync(ct);

        // TokenHash is never loaded. Scopes are reconstructed from the two flags in memory after the
        // projection, so the hash never sits next to the export payload even briefly.
        var apiTokenRows = await _db.ApiTokens
            .Where(t => t.UserId == userId)
            .Select(t => new { t.Name, t.TokenPrefix, t.CanRead, t.CanPlay, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt })
            .ToListAsync(ct);
        var apiTokens = apiTokenRows.Select(t =>
        {
            var scopes = new List<string>(2);
            if (t.CanRead) scopes.Add("read");
            if (t.CanPlay) scopes.Add("play");
            return new AccountExportApiTokenDto(
                t.Name, t.TokenPrefix, scopes, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt);
        }).ToArray();

        var roles = await _users.GetRolesAsync(user);

        // Phase 9d.2: the FACT that 2FA is on is exported (Art. 20 covers it); the shared TOTP key and
        // the recovery codes never are - they are credentials, in the same class as PasswordHash,
        // which scope decision 5 above already excludes from the export.
        // Patron badge: Tier is PatronBadge.For, never the raw column, so a lapsed one-off exports
        // as None exactly as it reads everywhere else.
        var patronTier = PatronBadge.For(user, DateTimeOffset.UtcNow);
        var profile = new AccountExportProfileDto(
            user.Id, user.Email, user.EmailConfirmed, user.DisplayName,
            user.CreatedAt, user.DisplayNameChangedAt,
            user.TermsAcceptedAt, user.TermsVersion, user.AgeConfirmedAt,
            user.TwoFactorEnabled,
            user.PairingFoundEmailUnsubscribedAt, user.RematchOfferedEmailUnsubscribedAt,
            user.IsPatron, patronTier.ToString(), user.PatronKind.ToString(),
            user.PatronSince, user.PatronExpiresAt,
            roles.ToArray(),
            user.AvatarImage is { Length: > 0 } ? Convert.ToBase64String(user.AvatarImage) : null);

        var truncation = new AccountExportTruncationDto(gamesTruncated, GamesCap, ratingHistoryTruncated, RatingHistoryCap);

        return new AccountExportDto(
            ExportVersion, DateTimeOffset.UtcNow, profile, ratings, historyRows, games,
            reportsFiled, reportsAgainst, sanctions, sessions, apiTokens, truncation);
    }
}
