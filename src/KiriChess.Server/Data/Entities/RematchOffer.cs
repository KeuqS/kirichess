namespace KiriChess.Server.Data.Entities;

/// <summary>
/// A live rematch offer for one finished game (Phase 11a.1). Keyed by the finished game's own id: at
/// most one live offer per finished game exists, a second <c>OfferRematch</c> call just overwrites who
/// offered and when rather than adding a second row. <see cref="AcceptedAt"/>/<see cref="AcceptedById"/>
/// are set together, exactly once, by the guarded <c>ExecuteUpdateAsync</c> in
/// <see cref="Games.RematchService"/>, the same shape as <c>Challenge</c>'s own accept guard
/// (Phase 9d.8's scope decision 3) - see that type's doc comment for why a bare in-memory
/// claim is not enough here: two accepts can land on two different server instances in the same second.
/// </summary>
public sealed class RematchOffer
{
    public Guid GameId { get; set; }
    public Guid OfferedById { get; set; }
    public DateTimeOffset OfferedAt { get; set; }
    public Guid? AcceptedById { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? NewGameId { get; set; }

    /// <summary>
    /// When the delayed rematch-offer mail becomes sendable (P-3-7). Null when the notifier is
    /// disabled (<c>Rematch:NotifyDelaySeconds</c> 0) or the owed send was skipped. Written on
    /// <c>Arm</c> from <c>DateTimeOffset.UtcNow</c> + delay, not <c>IGameClock</c>: the notifier is
    /// a singleton and does not take the clock today.
    /// </summary>
    public DateTimeOffset? NotificationDueAt { get; set; }

    /// <summary>
    /// When the rematch-offer mail actually went out. The send path claims this with
    /// <c>ExecuteUpdateAsync</c> where it is still null, so an in-process timer and a startup sweep
    /// cannot both mail.
    /// </summary>
    public DateTimeOffset? NotificationSentAt { get; set; }
}
