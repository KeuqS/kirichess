namespace KiriChess.Server.Data.Entities;

/// <summary>Per-game before/after rating snapshot, for rating graphs later (Phase 5 populates it).</summary>
public sealed class RatingHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Game? Game { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Pool { get; set; } = string.Empty;

    public double ValueBefore { get; set; }
    public double ValueAfter { get; set; }
    public double RdBefore { get; set; }
    public double RdAfter { get; set; }
    public double VolatilityBefore { get; set; }
    public double VolatilityAfter { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
