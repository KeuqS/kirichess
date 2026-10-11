namespace KiriChess.Server.Games;

/// <summary>One server-to-client push. <paramref name="Payload"/> is null for an event that carries no argument.</summary>
public sealed record HubEvent(string Hub, string Name, Type? Payload);

/// <summary>One client-callable hub method. <paramref name="Result"/> is null for a method returning plain <c>Task</c>.</summary>
public sealed record HubMethod(string Hub, string Name, Type? Result);

/// <summary>
/// The SignalR wire surface, declared once (Phase 9e.9). OpenAPI describes HTTP, so before this
/// existed the hub contract was fourteen string literals at fourteen call sites and eleven records
/// that appeared in no document, mirrored by hand in ui/lib/types.ts, which is the arrangement 9d.7
/// removed from every HTTP DTO and could not reach here.
///
/// This list is not documentation, three things read it:
///   - HubPayloadDocumentTransformer, which puts <see cref="PayloadTypes"/> into the OpenAPI document
///     so ui/lib/api.generated.ts carries them,
///   - HubContractTests, which fails if a SendAsync/InvokeAsync in src/ or a conn.on/invoke in ui/
///     names something not declared here,
///   - HubContractTests again, which fails if a public method appears on a hub class without one.
///
/// Adding a hub event therefore means adding a line here. That is the entire point: the guards can
/// see this file, and they cannot see a string literal you forgot to mirror.
/// </summary>
public static class HubContract
{
    public const string GameHub = "/hubs/game";
    public const string PresenceHub = "/hubs/presence";

    public static readonly IReadOnlyList<HubEvent> ServerEvents =
    [
        new(GameHub, "MovePlayed", typeof(MovePlayedDto)),
        new(GameHub, "GameEnded", typeof(GameEndedDto)),
        new(GameHub, "DrawOffered", typeof(DrawOfferedDto)),
        // No payload: the decline is the whole message, and the offer it answers is already known to
        // the client that made it.
        new(GameHub, "DrawDeclined", null),
        new(GameHub, "MatchFound", typeof(MatchFoundDto)),
        new(GameHub, "SpectatorsChanged", typeof(SpectatorsChangedDto)),
        new(GameHub, "OpponentDisconnected", typeof(OpponentDisconnectedDto)),
        // No payload: the counterpart of OpponentDisconnected, and there is nothing to say beyond
        // "they are back".
        new(GameHub, "OpponentReconnected", null),
        new(GameHub, "ChallengeAccepted", typeof(ChallengeAcceptedEvent)),
        // No payload: scoped to the one game's group both players are still connected to (Phase
        // 11a.1), there is nothing to name beyond "the other player wants a rematch".
        new(GameHub, "RematchOffered", null),
        new(GameHub, "RematchAccepted", typeof(RematchAcceptedDto)),
        // Sent with InvokeAsync<long>, not SendAsync (LatencyProbe.cs): a SignalR *client result*, so
        // the server does carry a return value off it, but nothing is sent *to* the client except the
        // call itself. The measurement is the round-trip time, never what the client answers, which is
        // why the payload is null on both sides. See LatencyProbe's doc comment.
        new(GameHub, "Ping", null),
        new(PresenceHub, "PresenceChanged", typeof(PresenceDto)),
    ];

    public static readonly IReadOnlyList<HubMethod> ClientMethods =
    [
        new(GameHub, "JoinGame", typeof(GameSnapshotDto)),
        new(GameHub, "SubmitMove", null),
        new(GameHub, "Resign", null),
        new(GameHub, "OfferDraw", null),
        new(GameHub, "RespondDraw", null),
        new(GameHub, "JoinQueue", null),
        new(GameHub, "LeaveQueue", null),
        new(GameHub, "WatchChallenge", null),
        new(GameHub, "UnwatchChallenge", null),
        new(GameHub, "OfferRematch", null),
        new(GameHub, "AcceptRematch", null),
        // The one anonymous-reachable method (Phase 8.1). See GameHub's class comment.
        new(GameHub, "JoinAsSpectator", typeof(SpectatorSnapshotDto)),
    ];

    /// <summary>
    /// A type that crosses a hub only nested inside another payload's own properties, never as the
    /// direct payload of a <see cref="HubEvent"/> or the direct result of a <see cref="HubMethod"/>.
    /// Today that is exactly <see cref="SpectatorMoveDto"/>, now nested in two snapshots rather than
    /// one: <see cref="SpectatorSnapshotDto.Moves"/> and, since C-1-2, <see cref="GameSnapshotDto.Moves"/>.
    /// There is still no "a move happened" event of its own: spectators see each ply over the same
    /// MovePlayed event, but that send is a <see cref="MovePlayedDto"/> whose
    /// <see cref="MovePlayedDto.LegalMoves"/> list is empty (C-2-9 = C-8-7). Declared here so
    /// HubPayloadDocumentTransformer still gives it its own component schema and a real $ref rather
    /// than an inline copy nested three levels deep inside each snapshot.
    /// </summary>
    public sealed record NestedPayload(Type Type, string Hub);

    public static readonly IReadOnlyList<NestedPayload> NestedPayloadTypes =
    [
        new(typeof(SpectatorMoveDto), GameHub),
    ];

    /// <summary>
    /// Every type that crosses a hub, in a stable order. Stable matters: the document transformer
    /// appends to components.schemas in this order, and docs/api/openapi.json is compared
    /// byte-for-byte by OpenApiContractTests.Committed_document_is_up_to_date.
    /// </summary>
    public static IReadOnlyList<Type> PayloadTypes { get; } =
        ServerEvents.Select(e => e.Payload)
            .Concat(ClientMethods.Select(m => m.Result))
            .Concat(NestedPayloadTypes.Select(n => n.Type))
            .OfType<Type>()
            .Distinct()
            .ToArray();
}
