using System.Text.Json;
using System.Text.Json.Nodes;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Puts the SignalR hub payloads into the OpenAPI document (Phase 9e.9). OpenAPI describes HTTP: every
/// component schema this document already carries is reachable from some path, through a request or a
/// response. The types in <see cref="HubContract.PayloadTypes"/> are pushed over
/// <c>/hubs/game</c> and <c>/hubs/presence</c> instead and appear on no path at all, so this
/// transformer adds them to <c>components.schemas</c> anyway, each marked with the
/// <c>x-kirichess-transport</c> extension naming the hub and the event or method it belongs to, so a
/// reader of the document can tell a hub payload from an orphaned HTTP schema rather than guessing.
///
/// <paramref name="publicDocument"/> is the published bot document. A <c>kirichess_pat_</c> token can
/// authenticate to <c>/hubs/game</c> and cannot authenticate to <c>/hubs/presence</c>
/// (<see cref="KiriChess.Server.Auth.HubAuthOverlayMiddleware"/>), so that document emits only the
/// game-hub payloads a play token can reach. The first-party document still carries both hubs, which is what
/// <c>ui/lib/api.generated.ts</c> is generated from.
///
/// Why one document rather than a second one for the hubs: every hub envelope composes
/// <see cref="GameStateResponse"/> or <see cref="ClockEventDto"/>, which are already components here
/// because HTTP responses use them. A separate hub document would have to either duplicate those two
/// schemas, which is precisely the drift bug this phase exists to kill, or carry a cross-document
/// <c>$ref</c> that <c>openapi-typescript</c> resolves awkwardly and that makes the two files
/// order-dependent on each other. See Phase 9e.9, scope decision 1, for the full
/// argument and the two rejected alternatives.
///
/// Runs in two passes over the payloads selected for this document. <see cref="OpenApiDocumentTransformerContext.GetOrCreateSchemaAsync"/>
/// produces a schema with every composed type inlined rather than referenced, the same way it would for
/// any other type walked from a route, so pass one adds a schema for every payload type and pass two
/// walks back over what was just added and rewrites each composed <see cref="GameStateResponse"/>,
/// <see cref="ClockEventDto"/>, or hub-payload-typed property into a real <c>$ref</c>. The rewrite has
/// to be a second pass, not folded into the first, because a <c>$ref</c> to a component that has not
/// been added to <c>document.Components.Schemas</c> yet has nothing to resolve against, and pass one is
/// what adds it.
/// </summary>
public sealed class HubPayloadDocumentTransformer(bool publicDocument) : IOpenApiDocumentTransformer
{
    /// <summary>
    /// HTTP-side types a hub payload may compose that already have a component schema of their own,
    /// because some HTTP response already uses them (<see cref="GameStateResponse"/> is
    /// <c>GET /api/games/{id}</c>'s body among others, <see cref="ClockEventDto"/> is
    /// <c>MoveResponse.Events</c>'s element type). Keyed by <c>typeof(...)</c>, never by a string
    /// literal, so renaming either record is a compile error here rather than a silent inline copy the
    /// next time the document is regenerated.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> HttpComponents = new Dictionary<Type, string>
    {
        [typeof(GameStateResponse)] = nameof(GameStateResponse),
        [typeof(ClockEventDto)] = nameof(ClockEventDto),
    };

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();

        // Pass 1: every payload type gets a schema, inline composition and all. All of them must be
        // present in components before pass 2 starts, because a $ref to a component that does not
        // exist yet has nothing to resolve against.
        // Order is HubContract.PayloadTypes, which OpenApiContractTests compares byte-for-byte.
        // Filtering with Where keeps that order; it only drops /hubs/presence from the public document.
        var payloads = publicDocument
            ? HubContract.PayloadTypes.Where(IsGameHubPayload).ToArray()
            : HubContract.PayloadTypes;

        var created = new List<(Type Type, OpenApiSchema Schema)>();
        foreach (var type in payloads)
        {
            var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);
            document.Components.Schemas[type.Name] = schema;
            created.Add((type, schema));
        }

        // Every type referenceable from a hub payload's own properties: the payload types this
        // document actually emitted (so e.g. SpectatorSnapshotDto.Moves can point at SpectatorMoveDto),
        // plus the two HTTP components above. Built once, keyed by CLR type, so substitution below is
        // a dictionary lookup rather than a string comparison. The public document must not $ref a
        // presence schema it deliberately did not add.
        var referenceable = payloads.ToDictionary(type => type, type => type.Name);
        foreach (var (httpType, name) in HttpComponents)
            referenceable[httpType] = name;

        // Pass 2: replace each inlined composed type with a reference to its component. Without this,
        // MovePlayedDto.state is a verbatim copy of GameStateResponse instead of a pointer at it, and
        // the generated TypeScript grows a second structurally-identical declaration of the largest
        // DTO in the codebase, four times over.
        foreach (var (type, schema) in created)
        {
            SubstituteReferences(type, schema, document, referenceable);
            Mark(type, schema);
        }
    }

    /// <summary>
    /// Rewrites every property of <paramref name="schema"/> whose CLR type is in
    /// <paramref name="referenceable"/> from an inline copy into a <c>$ref</c>. Two shapes exist among
    /// today's hub payloads: a direct property (<c>MovePlayedDto.State</c>) and a list element
    /// (<c>MovePlayedDto.Events</c>, <c>SpectatorSnapshotDto.Moves</c>) - both are handled, because both
    /// are actually present, not because either is hypothetical.
    /// </summary>
    private static void SubstituteReferences(
        Type type, OpenApiSchema schema, OpenApiDocument document, IReadOnlyDictionary<Type, string> referenceable)
    {
        if (schema.Properties is not { } properties)
            return;

        foreach (var property in type.GetProperties())
        {
            // JsonSchemaExporter names schema properties the same way System.Text.Json serializes
            // them, camelCase here because that is what the minimal-API JsonOptions this document is
            // built from configures. A property the exporter dropped (there are none among today's
            // hub payloads, every one is a plain public property) simply has no key to rewrite.
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!properties.TryGetValue(jsonName, out var propertySchema))
                continue;

            if (referenceable.TryGetValue(property.PropertyType, out var directName))
            {
                properties[jsonName] = new OpenApiSchemaReference(directName, document);
                continue;
            }

            // The array/list branch. string implements IEnumerable but is not itself generic, so
            // IsGenericType alone excludes it without a separate `!= typeof(string)` check; every
            // collection property on a hub payload is an IReadOnlyList<T>, which is generic.
            if (property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericArguments() is [var elementType] &&
                referenceable.TryGetValue(elementType, out var itemName) &&
                propertySchema is OpenApiSchema concretePropertySchema)
            {
                concretePropertySchema.Items = new OpenApiSchemaReference(itemName, document);
            }
        }
    }

    /// <summary>
    /// Writes the <c>x-kirichess-transport</c> extension: the hub the payload crosses, and the event
    /// name and/or method name it is attached to (a type used by both would carry both; none does
    /// today, but the lookup does not assume otherwise). A type declared only in
    /// <see cref="HubContract.NestedPayloadTypes"/> (only <see cref="Games.SpectatorMoveDto"/> today)
    /// carries the hub with no event or method, honestly reflecting that it never travels as its own
    /// addressable message, only nested inside one that does. This is the whole answer to "why is this
    /// schema referenced by no path", stated on the schema itself rather than left for a reader to
    /// reverse-engineer from HubContract.cs.
    /// </summary>
    private static void Mark(Type type, OpenApiSchema schema)
    {
        var asEvent = HubContract.ServerEvents.FirstOrDefault(e => e.Payload == type);
        var asMethod = HubContract.ClientMethods.FirstOrDefault(m => m.Result == type);
        var asNested = HubContract.NestedPayloadTypes.FirstOrDefault(n => n.Type == type);
        var hub = asEvent?.Hub ?? asMethod?.Hub ?? asNested?.Hub
            ?? throw new InvalidOperationException(
                $"{type.Name} is in HubContract.PayloadTypes but is not the payload of a declared " +
                "ServerEvent, the result of a declared ClientMethod, or a declared NestedPayload. " +
                "That should be unreachable, PayloadTypes is derived from those three lists.");

        var transport = new JsonObject { ["hub"] = hub };
        if (asEvent is not null) transport["event"] = asEvent.Name;
        if (asMethod is not null) transport["method"] = asMethod.Name;

        schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        schema.Extensions["x-kirichess-transport"] = new JsonNodeExtension(transport);
    }

    /// <summary>
    /// True when <paramref name="type"/> crosses <see cref="HubContract.GameHub"/>. A type that
    /// also crossed <see cref="HubContract.PresenceHub"/> would still be included: a play token can
    /// reach it on the game hub. Today no payload is shared between the two hubs.
    /// </summary>
    private static bool IsGameHubPayload(Type type) =>
        HubContract.ServerEvents.Any(e => e.Payload == type && e.Hub == HubContract.GameHub)
        || HubContract.ClientMethods.Any(m => m.Result == type && m.Hub == HubContract.GameHub)
        || HubContract.NestedPayloadTypes.Any(n => n.Type == type && n.Hub == HubContract.GameHub);
}
