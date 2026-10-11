namespace KiriChess.Server.Data.Entities;

/// <summary>Why a player was reported. Intake vocabulary only, nothing acts on these yet (Phase 8.5).</summary>
public enum ReportReason { EngineAssistance, Sandbagging, Stalling, Abuse, Other }

/// <summary>Where a report sits in the (not yet built) review pipeline. Every new report is Open.</summary>
public enum ReportStatus { Open, Reviewed, Dismissed }

/// <summary>
/// A player-submitted report against another player, optionally scoped to one game. Phase 6e.2 builds
/// the *intake*: rows land here. Phase 8.5 builds the review columns below, so moderation tooling can
/// read and resolve them instead of psql. Deliberately carries no score and no automated verdict.
/// </summary>
public sealed class Report
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReporterId { get; set; }
    public Guid ReportedUserId { get; set; }
    public Guid? GameId { get; set; }
    public ReportReason Reason { get; set; }
    /// <summary>Free text from the reporter; capped so a report cannot be a storage attack.</summary>
    public string? Details { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when a moderator resolves the report (Phase 8.5). Null while Status is Open.</summary>
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>Moderator-only. Never returned by /api/reports/mine - the reporter sees status, not reasoning.</summary>
    public string? ModeratorNote { get; set; }

    public ApplicationUser? Reporter { get; set; }
    public ApplicationUser? ReportedUser { get; set; }
    public Game? Game { get; set; }
}
