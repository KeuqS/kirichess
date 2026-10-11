using System.Net;
using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation;

/// <summary>
/// Minimal-API endpoints for the Phase 6e.2 report intake. Reports are stored, not acted on,
/// moderation tooling (queues, review, bans) is Phase 8.5. There is deliberately no endpoint that
/// lists reports filed *against* a user, and no endpoint that lists anyone else's reports: both
/// would be moderation surface, and the first would tell a cheater they were caught.
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports", async (
            CreateReportRequest req, ClaimsPrincipal user, KiriChessDbContext db,
            IEmailSender emailSender, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var reporterId = me.Value;

            if (!await db.Users.AnyAsync(u => u.Id == req.ReportedUserId, ct))
                return Results.BadRequest(new { error = "Unknown user." });

            if (req.ReportedUserId == reporterId)
                return Results.BadRequest(new { error = "You cannot report yourself." });

            if (!Enum.TryParse<ReportReason>(req.Reason, ignoreCase: true, out var reason))
                return Results.BadRequest(new { error = $"Unknown reason '{req.Reason}'. Known: {string.Join(", ", Enum.GetNames<ReportReason>())}" });

            string? details = req.Details?.Trim();
            if (details is { Length: 0 }) details = null;
            if (details is not null && details.Length > 1000)
                return Results.BadRequest(new { error = "Details must be 1000 characters or fewer." });

            if (req.GameId is { } gameId)
            {
                var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, ct);
                var reporterWasPlayer = game is not null &&
                    (game.WhitePlayerId == reporterId || game.BlackPlayerId == reporterId);
                var reportedWasOtherPlayer = game is not null &&
                    (game.WhitePlayerId == req.ReportedUserId || game.BlackPlayerId == req.ReportedUserId) &&
                    req.ReportedUserId != reporterId;
                if (!reporterWasPlayer || !reportedWasOtherPlayer)
                    return Results.BadRequest(new { error = "That game is not yours to report." });
            }

            // Duplicate suppression: an already-open report from the same reporter, against the same
            // user, for the same game (GameId equal, including both null) is not a second row, a
            // double-clicked Submit button should not create noise.
            var existing = await db.Reports.FirstOrDefaultAsync(r =>
                r.ReporterId == reporterId &&
                r.ReportedUserId == req.ReportedUserId &&
                r.GameId == req.GameId &&
                r.Status == ReportStatus.Open, ct);
            if (existing is not null)
                return Results.Ok(ToDto(existing));

            var report = new Report
            {
                ReporterId = reporterId,
                ReportedUserId = req.ReportedUserId,
                GameId = req.GameId,
                Reason = reason,
                Details = details
            };
            db.Reports.Add(report);
            await db.SaveChangesAsync(ct);

            // S-3-5: this used to be pull-only - a report landed here and nothing told the moderator
            // a queue existed to drain. One low-volume mail per genuinely new report (not the
            // duplicate-suppression path above, which returns before this line) is proportionate for
            // a one-person mod team: it is the difference between "review whenever I happen to open
            // /moderation" and "review because something asked me to". Best-effort - a mail provider
            // hiccup must not fail report intake, so a failure here is logged and swallowed, never
            // surfaced to the reporter.
            await NotifyAdminsAsync(report, config, emailSender, logger, ct);

            return Results.Created($"/api/reports/{report.Id}", ToDto(report));
        }).RequireAuthorization()
          // Phase 6e.1: the policy is defined by the rate-limiting task and wired here at
          // integration, since a report is a cheap write an abuser could otherwise spray.
          .RequireRateLimiting(RateLimitPolicies.Reports)
          // 200 when an already-open duplicate report is returned instead of created (see the
          // duplicate-suppression comment above); 201 for a genuinely new report.
          .Produces<ReportDto>(StatusCodes.Status200OK)
          .Produces<ReportDto>(StatusCodes.Status201Created)
          .Produces(StatusCodes.Status401Unauthorized)
          .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/reports/mine", async (ClaimsPrincipal user, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var rows = await db.Reports
                .Where(r => r.ReporterId == me.Value)
                .OrderByDescending(r => r.CreatedAt)
                .Take(50)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToArray());
        })
        .RequireAuthorization()
        .Produces<ReportDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static ReportDto ToDto(Report r) => new(
        r.Id, r.ReportedUserId, r.GameId, r.Reason.ToString(), r.Details, r.Status.ToString(), r.CreatedAt);

    /// <summary>
    /// S-3-5. One plain-text/HTML mail per new report to every address in
    /// <c>Moderation:AdminEmails</c> (read at request time, the same "never read config eagerly"
    /// rule <see cref="Auth.AuthEndpoints"/> already follows for <c>Auth:RequireConfirmedEmail</c> -
    /// <see cref="ModerationOptions"/> is not registered in the container, so this reads
    /// <see cref="IConfiguration"/> directly rather than needing a new DI registration).
    ///
    /// Deliberately does not include <see cref="Report.Details"/>: that field is free text a reporter
    /// wrote (S-1-10 already flags it as rendered user content), and a mail client renders HTML - the
    /// admin's own inbox is not a context worth widening that surface into. The reason and the two
    /// account ids are enough to say "something is waiting"; the full report is one click away in
    /// <c>/moderation</c>. Best-effort: an exception here is logged and swallowed, never allowed to
    /// fail report intake.
    /// </summary>
    private static async Task NotifyAdminsAsync(
        Report report, IConfiguration config, IEmailSender emailSender, ILogger logger, CancellationToken ct)
    {
        var adminEmails = config.GetSection("Moderation:AdminEmails").Get<string[]>() ?? [];
        if (adminEmails.Length == 0) return;

        var baseUrl = (config.GetValue("Frontend:BaseUrl", string.Empty) ?? string.Empty).TrimEnd('/');
        var queueUrl = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/moderation";
        var reasonText = report.Reason.ToString();

        var intro = $"A new report was filed (reason: {reasonText}). Reported user: {report.ReportedUserId}.";
        var linkLine = queueUrl is null ? "Open the moderation queue to review it." : queueUrl;
        var text = $"{intro}\n\n{linkLine}\n\n-- KiriChess";

        var htmlLink = queueUrl is null
            ? "Open the moderation queue to review it."
            : $"""<a href="{WebUtility.HtmlEncode(queueUrl)}">{WebUtility.HtmlEncode(queueUrl)}</a>""";
        var html = $"""
            <!doctype html>
            <html lang="en">
              <body style="margin:0;padding:24px;background:#282c34;color:#abb2bf;font-family:Montserrat,Arial,Helvetica,sans-serif;font-size:14px;line-height:1.6;">
                <p>{WebUtility.HtmlEncode(intro)}</p>
                <p>{htmlLink}</p>
              </body>
            </html>
            """;

        foreach (var toEmail in adminEmails)
        {
            try
            {
                await emailSender.SendAsync(
                    new EmailMessage(toEmail, "New report filed on KiriChess", text, html), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send report-filed notification to {ToEmail}", toEmail);
            }
        }
    }

}
