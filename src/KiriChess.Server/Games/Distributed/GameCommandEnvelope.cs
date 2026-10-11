namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// The forwarding RPC envelope (Phase 8.4). Pinned verbatim in Phase 8.4 so Tasks
/// C/D/E, none of which can see this task's code, never have to guess this shape. Published as a
/// JSON string (never a binary <c>RedisValue</c>) to <c>{p}game:cmd:{ownerInstanceId}</c>, so a human
/// can <c>SUBSCRIBE</c> to it in <c>redis-cli</c> during an incident.
/// </summary>
/// <param name="CorrelationId">Matches the pending <see cref="GameCommandReply"/>, see <see cref="GameCommandRouter"/>.</param>
/// <param name="FromInstanceId">Where the reply goes: <c>{p}game:reply:{FromInstanceId}</c>.</param>
/// <param name="Kind">One of <see cref="GameCommandKind"/>'s constants.</param>
/// <param name="CallerConnectionId">
/// The originating connection, carried across the wire so the owner's broadcast, issued through
/// <c>IHubContext&lt;GameHub&gt;</c>, can still do <c>Clients.Client(connectionId)</c> or
/// <c>Clients.GroupExcept(group, connectionId)</c> for a caller that lives on a different instance. The
/// SignalR backplane makes both calls work across instances; this field is what lets them target the
/// right connection.
/// </param>
/// <param name="Arg">UCI for <see cref="GameCommandKind.SubmitMove"/>, "true"/"false" for <see cref="GameCommandKind.RespondDraw"/>, else null.</param>
public sealed record GameCommandEnvelope(
    string CorrelationId,
    string FromInstanceId,
    string Kind,
    Guid GameId,
    Guid UserId,
    string CallerConnectionId,
    string? Arg);

/// <summary>
/// Reply to a <see cref="GameCommandEnvelope"/>, published on <c>{p}game:reply:{FromInstanceId}</c>.
/// Pinned alongside <see cref="GameCommandEnvelope"/>.
/// </summary>
/// <param name="Error">
/// The exact message the local path would have thrown, rethrown verbatim as a <c>HubException</c> on
/// the calling instance. A player must not be able to tell from the message whether their action
/// executed locally or was forwarded, see <see cref="GameActionExecutor"/>.
/// </param>
/// <param name="Payload">JSON snapshot when the <see cref="GameCommandEnvelope.Kind"/> returns one, else null.</param>
public sealed record GameCommandReply(
    string CorrelationId,
    bool Ok,
    string? Error,
    string? Payload);

/// <summary>The fixed <see cref="GameCommandEnvelope.Kind"/> values, pinned in Phase 8.4's RPC contract table.</summary>
public static class GameCommandKind
{
    public const string JoinGame = "JoinGame";
    public const string SubmitMove = "SubmitMove";
    public const string Resign = "Resign";
    public const string OfferDraw = "OfferDraw";
    public const string RespondDraw = "RespondDraw";
    public const string Abandon = "Abandon";
    public const string JoinAsSpectator = "JoinAsSpectator";
}
