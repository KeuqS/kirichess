namespace KiriChess.Server.Data.Entities;

/// <summary>
/// One row per <see cref="Infrastructure.KillSwitch"/> (Phase 10c.4), keyed by the enum's name so the
/// table stays the two-column shape an incident actually needs. Seeded to <c>Enabled = true</c> for
/// all four members by the migration that creates it: an incident responder flips one to
/// <c>false</c> through <c>KillSwitchEndpoints</c>, and every running instance picks it up within
/// <see cref="Infrastructure.KillSwitchService"/>'s cache window - no deploy.
/// </summary>
public sealed class KillSwitchState
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Who last flipped it. Null only for the migration's own seed rows, never for a switch that has actually been touched.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
