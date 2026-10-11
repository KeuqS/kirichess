namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Every mail category a player can opt out of independently (Phase 11b.1). Deliberately excludes
/// <see cref="EmailTemplates.Confirmation"/> and <see cref="EmailTemplates.PasswordReset"/>: those are
/// security-critical, not "reach-back" mail, and carry no unsubscribe link, the same distinction every
/// mail provider's own compliance rules draw. Each member's name is also its query-string value on the
/// unsubscribe link (<see cref="NotificationPreferenceService"/>'s <c>ParseCategory</c>), so renaming
/// one silently breaks every already-sent email still carrying the old name - add, never rename.
/// </summary>
public enum NotificationCategory
{
    /// <summary>"Your challenge was accepted" (Phase 9e.5).</summary>
    PairingFound,

    /// <summary>"Your opponent wants a rematch" (Phase 11b.1).</summary>
    RematchOffered,
}
