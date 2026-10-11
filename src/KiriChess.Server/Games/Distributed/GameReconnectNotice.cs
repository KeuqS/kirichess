namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// Published to <c>{p}game:reconnect</c> whenever <see cref="GameConnectionTracker.Track"/> records a
/// connection (Phase 8.4 Step 6.2). Every instance subscribes (via <see cref="GameCommandRouter"/>) and
/// calls <see cref="GameConnectionTracker.HandleReconnectAsync"/> locally on receipt, only the
/// instance actually holding the armed abandon timer for (GameId, UserId) does anything; everyone else,
/// including the instance that published the notice, is a silent no-op via
/// <see cref="GameConnectionTracker.CancelPendingAbandon"/>'s remove-once semantics, so the notice can
/// be delivered unconditionally without any instance needing to know in advance who is armed.
///
/// Carries <see cref="ConnectionId"/> in addition to the README key-schema table's illustrative
/// <c>{"gameId":…,"userId":…}</c> example: the broadcast that fires on a genuine cancellation must
/// exclude the reconnecting connection (<c>Clients.GroupExcept(group, connectionId)</c>), and that
/// connection id only ever exists on the instance about to publish this notice, nobody else could
/// supply it later.
/// </summary>
public sealed record GameReconnectNotice(Guid GameId, Guid UserId, string ConnectionId);
