using System.Security.Claims;
using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Ratings;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation;

/// <summary>
/// Minimal-API endpoints for the Phase 8.5 moderation surface: the report queue, sanction issuance and
/// lifting, the per-user dossier, and the sanctioned player's own view of their sanctions. Every route
/// but the last carries <see cref="ModerationPolicies.Admin"/> - see Phase 8.5,
/// "Detector output is admin-only, on every route, forever" for why this is never disguised as a 404
/// the way an ongoing game is: a logged-in player already knows moderation exists, since they can file
/// reports themselves.
/// </summary>
public static class ModerationEndpoints
{
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/moderation").RequireAuthorization(ModerationPolicies.Admin);

        group.MapGet("/reports", async (string? status, int? take, int? skip, KiriChessDbContext db, CancellationToken ct) =>
        {
            var takeClamped = Math.Clamp(take ?? 50, 1, 200);
            var skipValue = Math.Max(skip ?? 0, 0);

            var query = db.Reports.AsQueryable();
            var statusValue = status ?? "Open";
            if (!statusValue.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<ReportStatus>(statusValue, ignoreCase: true, out var parsedStatus))
                    return Results.BadRequest(new
                    {
                        error = $"Unknown status '{statusValue}'. Known: all, {string.Join(", ", Enum.GetNames<ReportStatus>())}"
                    });
                query = query.Where(r => r.Status == parsedStatus);
            }

            var rows = await query
                .OrderByDescending(r => r.CreatedAt)
                .Skip(skipValue).Take(takeClamped)
                .ToListAsync(ct);

            return Results.Ok(await ToReportDtosAsync(rows, db, ct));
        })
        .Produces<ModerationReportDto[]>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/reports/{id:guid}/resolve", async (
            Guid id, ResolveReportRequest req, ClaimsPrincipal caller, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (report is null) return Results.NotFound();

            if (report.Status != ReportStatus.Open)
                return Results.BadRequest(new { error = $"This report is already {report.Status}." });

            // Open is rejected: un-resolving a report is not a workflow this surface supports.
            if (!Enum.TryParse<ReportStatus>(req.Status, ignoreCase: true, out var newStatus) || newStatus == ReportStatus.Open)
                return Results.BadRequest(new { error = "Status must be 'Reviewed' or 'Dismissed'." });

            report.Status = newStatus;
            report.ReviewedByUserId = me.Value;
            report.ReviewedAt = DateTimeOffset.UtcNow;
            report.ModeratorNote = req.ModeratorNote;
            await db.SaveChangesAsync(ct);

            var dtos = await ToReportDtosAsync([report], db, ct);
            return Results.Ok(dtos[0]);
        })
        .Produces<ModerationReportDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/users/{userId:guid}", async (
            Guid userId, ClaimsPrincipal caller, KiriChessDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();

            // 9d.3: the one line this route was missing. IssuedByUserId already answers "who sanctioned
            // this player"; this answers "who looked", which a full dossier (email, report history,
            // sanction history) needs just as much - see Phase 9d.3
            //
            // S-3-6: this line, the sandbagging/engine detector run lines in Moderation/Detection/,
            // and Content/ContentEndpoints.cs's forced-rename line are the ONLY record of a moderator
            // *reading* something - unlike a UserSanction or Report.ReviewedByUserId row, none of it
            // is durable in Postgres. It answers "who looked" only for as long as the log sink keeps
            // it, which today is whatever Better Stack's plan default retention is (RETENTION.md §1
            // notes no explicit retention is set; I-9-5 owns changing that), not a property of this
            // codebase. With one moderator this is low-stakes. If a second moderator is ever added,
            // that assumption stops holding and this becomes the only thing that could answer a
            // question about the first one - an append-only audit table (moderator id, action,
            // subject id, timestamp) is the fix at that point, not before.
            logger.LogInformation("Moderator {ModeratorId} viewed the dossier for user {UserId}", me.Value, userId);

            var ratings = await db.Ratings
                .Where(r => r.UserId == userId)
                .Select(r => new ModerationRatingDto(
                    r.Pool, r.Value, r.RatingDeviation, r.GamesPlayed, RatingConstants.IsProvisional(r.RatingDeviation)))
                .ToArrayAsync(ct);

            var gamesPlayed = await db.Games.CountAsync(g =>
                (g.WhitePlayerId == userId || g.BlackPlayerId == userId) && g.Result != GameResult.Ongoing, ct);

            // Same predicate AbandonmentPolicy uses: Abandoned termination, and this user is the
            // loser. Reusing rather than reinventing "abandoned" keeps the two counts from disagreeing.
            var gamesAbandoned = await db.Games.CountAsync(g =>
                g.Termination == TerminationReason.Abandoned &&
                ((g.WhitePlayerId == userId && g.Result == GameResult.BlackWins) ||
                 (g.BlackPlayerId == userId && g.Result == GameResult.WhiteWins)), ct);

            var reportsAgainst = await db.Reports.CountAsync(r => r.ReportedUserId == userId, ct);
            var reportsFiled = await db.Reports.CountAsync(r => r.ReporterId == userId, ct);

            // S-2-6. The subject's own seats only. A solo game increments WhiteDisconnectCount and
            // leaves black at 0, so the second sum excludes the case where both seats are this user
            // and the two sums would otherwise count one drop twice.
            var graceAsWhite = await db.Games
                .Where(g => g.WhitePlayerId == userId)
                .SumAsync(g => (int?)g.WhiteDisconnectCount, ct) ?? 0;
            var graceAsBlack = await db.Games
                .Where(g => g.BlackPlayerId == userId && g.WhitePlayerId != userId)
                .SumAsync(g => (int?)g.BlackDisconnectCount, ct) ?? 0;

            var sanctionRows = await db.UserSanctions
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.IssuedAt)
                .ToListAsync(ct);
            var sanctionDtos = await ToSanctionDtosAsync(sanctionRows, db, ct);

            return Results.Ok(new ModerationUserDto(
                user.Id, user.DisplayName, user.Email ?? string.Empty, user.CreatedAt, user.EmailConfirmed,
                ratings, gamesPlayed, gamesAbandoned, reportsAgainst, reportsFiled, sanctionDtos,
                graceAsWhite + graceAsBlack));
        })
        .Produces<ModerationUserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/sanctions", async (
            bool? activeOnly, Guid? userId, int? take, int? skip, SanctionService sanctions, KiriChessDbContext db, CancellationToken ct) =>
        {
            var takeClamped = Math.Clamp(take ?? 50, 1, 200);
            var skipValue = Math.Max(skip ?? 0, 0);
            var rows = await sanctions.ListAsync(activeOnly ?? false, userId, takeClamped, skipValue, ct);
            return Results.Ok(await ToSanctionDtosAsync(rows, db, ct));
        })
        .Produces<SanctionDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/sanctions", async (
            CreateSanctionRequest req, ClaimsPrincipal caller, SanctionService sanctions, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (req.UserId == me.Value)
                return Results.BadRequest(new { error = "You cannot sanction yourself." });

            // One line that removes a whole category of incident: a moderator cannot be sanctioned,
            // even by another moderator.
            if (await sanctions.IsAdminAsync(req.UserId, ct))
                return Results.BadRequest(new { error = "This user is a moderator and cannot be sanctioned." });

            UserSanction? sanction;
            try
            {
                sanction = await sanctions.IssueAsync(
                    req.UserId, req.Type, req.Reason, req.ModeratorNote, req.ExpiresAt, req.ReportId, me.Value, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (sanction is null) return Results.BadRequest(new { error = "Unknown user." });

            var dtos = await ToSanctionDtosAsync([sanction], db, ct);
            return Results.Created($"/api/moderation/sanctions/{sanction.Id}", dtos[0]);
        })
        .Produces<SanctionDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/sanctions/{id:guid}/lift", async (
            Guid id, LiftSanctionRequest req, ClaimsPrincipal caller, SanctionService sanctions, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            UserSanction? sanction;
            try
            {
                sanction = await sanctions.LiftAsync(id, me.Value, req.Reason, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (sanction is null) return Results.NotFound();

            var dtos = await ToSanctionDtosAsync([sanction], db, ct);
            return Results.Ok(dtos[0]);
        })
        .Produces<SanctionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        // L-2-3: after the access token expires a banned account cannot sign in, so the self-serve
        // export is unreachable. Same builder, run by an admin after a mail to privacy@.
        group.MapGet("/users/{userId:guid}/export", async (
            Guid userId, ClaimsPrincipal caller, AccountExportBuilder exportBuilder,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var export = await exportBuilder.BuildAsync(userId, ct);
            if (export is null) return Results.NotFound();

            logger.LogInformation("Moderator {ModeratorId} exported account {UserId}", me.Value, userId);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportJsonOptions);
            return Results.File(bytes, "application/json",
                $"kirichess-export-{userId:N}-{DateTimeOffset.UtcNow:yyyyMMdd}.json");
        })
        .RequireRateLimiting(RateLimitPolicies.AccountData)
        .Produces<AccountExportDto>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        // L-2-3 / L-4-2: the same erasure as POST /api/account/delete, without requiring the
        // subject to be able to sign in. Sanctions survive (Restrict + tombstone).
        group.MapPost("/users/{userId:guid}/erase", async (
            Guid userId, EraseAccountRequest req, ClaimsPrincipal caller,
            AccountDeletionService deletion, SanctionService sanctions,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (userId == me.Value)
                return Results.BadRequest(new { error = "You cannot erase your own account on this route." });

            if (await sanctions.IsAdminAsync(userId, ct))
                return Results.BadRequest(new { error = "This user is a moderator and cannot be erased on this route." });

            if (req.Acknowledgement != "ERASE THIS ACCOUNT")
                return Results.BadRequest(new { error = "Type \"ERASE THIS ACCOUNT\" exactly to confirm." });

            var result = await deletion.DeleteAsync(userId, ct);
            if (result.Outcome == AccountDeletionOutcome.Deleted)
                logger.LogInformation("Moderator {ModeratorId} erased account {UserId}", me.Value, userId);

            return result.Outcome switch
            {
                AccountDeletionOutcome.NotFound or AccountDeletionOutcome.AlreadyDeleted => Results.NotFound(),
                AccountDeletionOutcome.GameInProgress => Results.Conflict(new
                {
                    error = "Finish or resign the current game before erasing this account."
                }),
                AccountDeletionOutcome.Deleted => Results.Ok(new DeleteAccountResponse(result.DeletedAt!.Value)),
                _ => throw new InvalidOperationException($"Unhandled {nameof(AccountDeletionOutcome)} '{result.Outcome}'.")
            };
        })
        .RequireRateLimiting(RateLimitPolicies.AccountData)
        .Produces<DeleteAccountResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        // Any authenticated user, not just admins - the one exception on this whole surface (see the
        // class doc). Active sanctions for the caller only: no note, no issuer, no id, no report id.
        app.MapGet("/api/moderation/my-sanctions", async (ClaimsPrincipal caller, SanctionService sanctions, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var rows = await sanctions.ForUserAsync(me.Value, ct);
            var now = DateTimeOffset.UtcNow;
            var active = rows
                .Where(s => s.IsActiveAt(now))
                .Select(s => new MySanctionDto(s.Type.ToString(), s.Reason, s.IssuedAt, s.ExpiresAt))
                .ToArray();
            return Results.Ok(active);
        })
        .RequireAuthorization()
        .Produces<MySanctionDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<ModerationReportDto[]> ToReportDtosAsync(
        List<Report> rows, KiriChessDbContext db, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        // One grouped query for the whole page, not one COUNT per row. S-3-4: this used to be
        // g.Count() - a count of open report ROWS against the user, not of distinct reporters. One
        // account can file a fresh report per game against the same target (ReportEndpoints'
        // duplicate suppression keys on (ReporterId, ReportedUserId, GameId, Open), so a different
        // GameId is a different row) inside the 20-per-hour budget, which raised this signal to red
        // on its own with nobody else involved. Distinct(r => r.ReporterId) makes the number mean
        // "how many different people complained", which is the number a moderator actually wants
        // when deciding what to look at first.
        var reportedIds = rows.Select(r => r.ReportedUserId).Distinct().ToArray();
        var openCounts = await db.Reports
            .Where(r => reportedIds.Contains(r.ReportedUserId) && r.Status == ReportStatus.Open)
            .GroupBy(r => r.ReportedUserId)
            .Select(g => new { UserId = g.Key, Count = g.Select(r => r.ReporterId).Distinct().Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var userIds = rows.SelectMany(r => new[] { r.ReporterId, r.ReportedUserId }).Distinct().ToArray();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return rows.Select(r => new ModerationReportDto(
            r.Id, r.ReporterId, names.GetValueOrDefault(r.ReporterId, "Unknown"),
            r.ReportedUserId, names.GetValueOrDefault(r.ReportedUserId, "Unknown"),
            r.GameId, r.Reason.ToString(), r.Details, r.Status.ToString(),
            r.ModeratorNote, r.ReviewedByUserId, r.ReviewedAt, r.CreatedAt,
            openCounts.GetValueOrDefault(r.ReportedUserId, 0))).ToArray();
    }

    private static async Task<SanctionDto[]> ToSanctionDtosAsync(
        List<UserSanction> rows, KiriChessDbContext db, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var userIds = rows.SelectMany(s => new[] { s.UserId, s.IssuedByUserId }).Distinct().ToArray();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = DateTimeOffset.UtcNow;

        return rows.Select(s => new SanctionDto(
            s.Id, s.UserId, names.GetValueOrDefault(s.UserId, "Unknown"),
            s.Type.ToString(), s.Reason, s.ModeratorNote,
            s.IssuedByUserId, names.GetValueOrDefault(s.IssuedByUserId, "Unknown"), s.IssuedAt,
            s.ExpiresAt, s.LiftedAt, s.LiftedByUserId, s.LiftReason, s.ReportId,
            s.IsActiveAt(now))).ToArray();
    }

}
