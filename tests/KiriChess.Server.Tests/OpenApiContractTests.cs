using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Guards the committed OpenAPI document (Phase 9d.7). Boots the app the same Docker-free way
/// <see cref="HealthEndpointTests"/> does: <c>UseEnvironment("Production")</c> plus an in-memory
/// configuration overlay, no database, no Redis, but feeds it
/// <see cref="OpenApiExport.DeterministicConfiguration"/> instead of ad hoc values, so this test and
/// the <c>--export-openapi</c> CLI mode agree on the exact configuration by construction: if they ever
/// diverged, this test could pass against a document the CLI could never actually reproduce.
///
/// The document is resolved from <c>factory.Services</c> directly (via
/// <see cref="OpenApiExport.SerializeAsync"/>), never over HTTP for the first-party document: that
/// document is never mapped as a route (Phase 9f.5). The published public document is served at
/// <c>/openapi/v1.json</c> in every environment.
/// </summary>
public sealed class OpenApiContractTests : IAsyncLifetime
{
    // 9d.7 scope decision 4's coverage guard (Every_api_operation_describes_its_success_response) walks
    // every operation and demands a described, schema-bearing 2xx/3xx response. These are the routes
    // that legitimately have none, and each is commented with why - an allowlist nobody can audit is
    // how a guard rots.
    private static readonly HashSet<(string Method, string Path)> NoJsonBodyAllowlist = new()
    {
        // 204 No Content: revoking a refresh token (single or all-for-user) has nothing to return.
        ("POST", "/api/auth/logout"),
        ("POST", "/api/auth/logout-all"),
        // 204 No Content: revoking one session from the profile page (9e.6).
        ("DELETE", "/api/account/sessions/{id}"),
        // 204 No Content: revoking an API token (9f.5). Same no-oracle shape as sessions.
        ("DELETE", "/api/account/api-tokens/{id}"),
        // 204 No Content: cancelling one's own challenge (9d.8). The challenge is gone; there is no
        // resource left to describe, and returning the tombstone would only invite a client to render it.
        ("DELETE", "/api/challenges/{token}"),
        // 204 No Content: cancelling a challenge from the public/bot API (9f.5).
        ("DELETE", "/api/v1/challenges/{token}"),
        // 200 application/x-chess-pgn: same as first-party PGN, a file not JSON.
        ("GET", "/api/v1/games/{id}/pgn"),
        // 302 Found: browser navigations, never fetched - see googleSignInUrl's doc comment in
        // ui/lib/api.ts and the comment on these two routes in ExternalAuthEndpoints.cs.
        ("GET", "/api/auth/external/google/start"),
        ("GET", "/api/auth/external/callback"),
        // 200 application/x-chess-pgn: the body IS the .pgn file (9e.2). Describing it with a JSON
        // schema would be a lie about the wire; the format it actually obeys is specified in
        // docs/game/PGN.md, which no OpenAPI schema can express.
        ("GET", "/api/games/{id}/pgn"),
        // 200 image/jpeg: the body IS the avatar (PublicProfileEndpoints.cs already declares that
        // content type via .Produces). Same category as the two PGN routes above: a JSON schema
        // would describe a body that is never sent. 404 when the user has no avatar is described
        // too, so the route is not underspecified, it is just not JSON.
        ("GET", "/api/users/{id}/avatar"),
        // 204 No Content: flipping a kill switch (10c.4). The caller already knows what it asked for;
        // GET /api/moderation/kill-switches is what returns the resulting state.
        ("PUT", "/api/moderation/kill-switches/{name}"),
        // 204 No Content: setting one mail preference (11b.1). The caller already knows what it
        // asked for; GET /api/account/notifications is what returns the resulting state.
        ("PUT", "/api/account/notifications"),
        // 204 No Content: the anonymous one-click unsubscribe (11b.1). Same no-oracle shape as
        // /api/auth/logout above - nothing distinguishes "already unsubscribed" from "just unsubscribed".
        ("POST", "/api/notifications/unsubscribe"),
        // 204 No Content: subscribing/unsubscribing one browser to web push (11b.1 follow-up). Same
        // shape as the mail preference above - the caller already knows what it sent, and there is no
        // GET counterpart because a subscription is per-device state the caller already holds locally.
        ("POST", "/api/push/subscribe"),
        ("POST", "/api/push/unsubscribe"),
        // 204 No Content: the anonymous counter beacon (AD.6 / 11a.3). Fire-and-forget on purpose -
        // a 200 JSON body would be an oracle for the key allowlist and a failed increment must never
        // surface to a visitor. Unknown keys also 204 and write nothing.
        ("POST", "/api/analytics/counter"),
        // 200 image/*: the body IS the avatar bytes (public profile). Describing it as JSON would
        // be a lie about the wire; Results.File sets the image content type.
        ("GET", "/api/users/{id}/avatar"),
    };

    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(OpenApiExport.DeterministicConfiguration));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Committed_document_is_up_to_date()
    {
        var generated = await OpenApiExport.SerializeAsync(_factory!.Services);

        var repoRoot = OpenApiExport.FindRepoRoot();
        var committedPath = Path.Combine(repoRoot, "docs", "api", "openapi.json");
        Assert.True(File.Exists(committedPath),
            $"{committedPath} does not exist. Generate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json");

        // Normalize \r\n to \n before comparing: a Windows checkout can carry CRLF line endings even
        // though the file was written LF-only, and this must not fail spuriously for that reason alone
        // - ui/scripts/gen-rules.mjs applies the same normalization for the same reason.
        var committed = (await File.ReadAllTextAsync(committedPath)).Replace("\r\n", "\n");

        Assert.True(string.Equals(generated, committed, StringComparison.Ordinal),
            "docs/api/openapi.json is stale. Regenerate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json");
    }

    [Fact]
    public async Task Every_api_operation_describes_its_success_response()
    {
        AssertSuccessResponses(await GetDocumentAsync());
    }

    [Fact]
    public async Task Every_public_api_operation_describes_its_success_response()
    {
        AssertSuccessResponses(await GetDocumentAsync("public"));
    }

    [Fact]
    public async Task Enum_valued_string_properties_declare_their_values()
    {
        var document = await GetDocumentAsync();
        var schemas = document.Components!.Schemas!;

        var sideToMove = schemas["GameStateResponse"].Properties!["sideToMove"];
        Assert.Equal(["White", "Black"], sideToMove.Enum!.Select(n => n!.ToString()).ToArray());

        var moveCostMode = schemas["EconomyDto"].Properties!["moveCostMode"];
        Assert.Equal(
            ["None", "Flat", "PerPiece", "Distance", "PerPieceXDistance"],
            moveCostMode.Enum!.Select(n => n!.ToString()).ToArray());
    }

    [Fact]
    public async Task No_numeric_property_is_documented_as_accepting_a_string()
    {
        // Pins NumericSchemaTransformer (Phase 9d.7, Task C): AllowReadingFromString makes .NET 10's
        // document generator honestly describe every numeric schema's *input* tolerance as
        // `["integer", "string"]` / `["number", "string"]`, which is a true fact about the deserializer
        // and a false one about the document's contract - openapi-typescript renders it as
        // `number | string`, which is strictly worse than the `number` the generated client replaces.
        // Walks every schema reachable from the document, not just components.schemas, because a
        // numeric query parameter (e.g. GET /api/leaderboard?limit=) carries its own inline schema that
        // components.schemas never sees.
        var document = await GetDocumentAsync();
        var offenders = new List<string>();

        void Visit(IOpenApiSchema? schema, string path)
        {
            if (schema is null)
                return;

            if (schema.Type is { } type)
            {
                var hasNumericFlag = type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number);
                if (hasNumericFlag && type.HasFlag(JsonSchemaType.String))
                    offenders.Add(path);
            }

            if (schema.Properties is { } properties)
                foreach (var (name, propertySchema) in properties)
                    Visit(propertySchema, $"{path}.{name}");

            Visit(schema.Items, $"{path}[]");
        }

        foreach (var (schemaName, schema) in document.Components!.Schemas!)
            Visit(schema, $"components.schemas.{schemaName}");

        foreach (var (path, item) in document.Paths!)
            foreach (var (method, operation) in item.Operations!)
                foreach (var parameter in operation.Parameters ?? [])
                    Visit(parameter.Schema, $"{method} {path} parameter '{parameter.Name}'");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} schema(s) declare a numeric type together with \"string\", which "
            + "NumericSchemaTransformer should have stripped. Check that it is registered in Program.cs "
            + "and that it runs for these sites:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order()));
    }

    // The property names this codebase actually uses for a closed, small vocabulary - not an exhaustive
    // list of every string property that could ever hold one, a heuristic over the names 9d.2 through
    // 9d.8 have actually chosen. A property whose name ends with one of these (case-insensitively, so
    // both "reason" and "liftReason" match) and is a bare `string` with no `enum` either forgot
    // [StringEnum]/[StringConstants] or belongs in FreeTextAllowlist below.
    private static readonly string[] VocabularyPropertyNames =
    [
        "colour", "color", "status", "verdict", "side", "sideToMove", "winner", "termination",
        "reason", "mode", "moveCostMode", "kind", "outcome", "state", "type",
    ];

    // Properties that match VocabularyPropertyNames by name but are genuinely free text, not a closed
    // vocabulary - an allowlist nobody can audit is how a guard rots, so each entry carries its own
    // one-line reason rather than a bare tuple.
    private static readonly HashSet<(string SchemaName, string PropertyName)> FreeTextAllowlist = new()
    {
        // Free text a moderator types when issuing a sanction; shown to the sanctioned player. There is
        // no fixed set of reasons, and forcing one would make the moderator's explanation worse, not
        // safer.
        ("SanctionDto", "reason"),
        ("MySanctionDto", "reason"),
        ("CreateSanctionRequest", "reason"),
        ("AccountExportSanctionDto", "reason"),
        // Free text a moderator types when lifting a sanction, for the same reason as SanctionDto.reason
        // above. Nullable because a sanction that was never lifted has none.
        ("SanctionDto", "liftReason"),
        ("LiftSanctionRequest", "reason"),
        // Free text stamped on the moderator audit trail when an admin overrides a display name - never
        // shown to the renamed player, and there is no fixed set of reasons an admin might have.
        ("ModeratorRenameRequest", "reason"),
        // Genuinely a closed two-value vocabulary (which side the exporting user played), but the wire
        // value is the literal lowercase "white"/"black" AccountExportBuilder writes for the export file
        // (a display convention), not Side's PascalCase "White"/"Black" - see the doc comment on
        // AccountExportGameDto. [StringEnum(typeof(Side))] would document values this endpoint never
        // actually sends, which is worse than no enum at all.
        ("AccountExportGameDto", "colour"),
        // RFC 9457's problem-type URI on ASP.NET's own HttpValidationProblemDetails, not ours to
        // annotate and not a closed set: it is a URI identifying the problem kind, and the framework
        // fills it in.
        ("HttpValidationProblemDetails", "type"),
    };

    [Fact]
    public async Task Vocabulary_valued_string_properties_declare_their_values()
    {
        // The other half of the same bug class as No_numeric_property_is_documented_as_accepting_a_string:
        // 9d.2 and 9d.8 both added a string property with a fixed, closed vocabulary and nobody applied
        // [StringEnum] or [StringConstants], because the earlier coverage guard
        // (Every_api_operation_describes_its_success_response) only checks that an operation describes A
        // response, not that the response is as precise as it should be. That let ChallengeDto.colour,
        // ChallengeDto.status, ChallengeAcceptedDto.yourColour, DetectionReportDto.verdict and
        // DormantAccountRowDto.reason all generate as bare `string` for the length of Task A and B, a
        // downgrade from the hand-written TypeScript they replaced.
        var document = await GetDocumentAsync();
        var gaps = new List<string>();

        foreach (var (schemaName, schema) in document.Components!.Schemas!)
        {
            if (schema.Properties is not { } properties)
                continue;

            foreach (var (propertyName, property) in properties)
            {
                var matchesVocabulary = VocabularyPropertyNames.Any(vocabulary =>
                    propertyName.EndsWith(vocabulary, StringComparison.OrdinalIgnoreCase));
                if (!matchesVocabulary)
                    continue;

                if (FreeTextAllowlist.Contains((schemaName, propertyName)))
                    continue;

                var isBareString = property.Type is { } type
                    && type.HasFlag(JsonSchemaType.String)
                    && (property.Enum is null || property.Enum.Count == 0);
                if (isBareString)
                    gaps.Add($"{schemaName}.{propertyName}");
            }
        }

        Assert.True(gaps.Count == 0,
            $"{gaps.Count} propert(y/ies) look like a closed vocabulary by name but generate as a bare "
            + "string with no enum. Apply [StringEnum(typeof(...))] if the value is a C# enum, "
            + "[StringConstants(typeof(...))] if it is a static class of const strings, or add the "
            + "property to FreeTextAllowlist with a reason if it is genuinely free text:"
            + Environment.NewLine + string.Join(Environment.NewLine, gaps.Order()));
    }

    [Fact]
    public async Task Committed_public_document_is_up_to_date()
    {
        var generated = await OpenApiExport.SerializeAsync(_factory!.Services, "public");

        var repoRoot = OpenApiExport.FindRepoRoot();
        var committedPath = Path.Combine(repoRoot, "docs", "api", "openapi-public.json");
        Assert.True(File.Exists(committedPath),
            $"{committedPath} does not exist. Generate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json");

        var committed = (await File.ReadAllTextAsync(committedPath)).Replace("\r\n", "\n");

        Assert.True(string.Equals(generated, committed, StringComparison.Ordinal),
            "docs/api/openapi-public.json is stale. Regenerate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json");
    }

    [Fact]
    public async Task Public_document_scopes_hub_payloads_to_the_game_hub()
    {
        // S-4-4: a kirichess_pat_ token can join /hubs/game and is anonymous on /hubs/presence, so the published
        // document must not describe the presence payload. The game-hub payloads stay, marked
        // x-kirichess-transport, because live clocks for a play token are still that hub.
        var document = await GetDocumentAsync("public");
        var schemas = document.Components!.Schemas!;

        var gameHub = HubPayloadNames(HubContract.GameHub);
        var presence = HubPayloadNames(HubContract.PresenceHub);

        Assert.NotEmpty(gameHub);
        Assert.NotEmpty(presence);
        Assert.All(gameHub, name => Assert.Contains(name, schemas.Keys));
        Assert.All(presence, name => Assert.DoesNotContain(name, schemas.Keys));
        Assert.Contains("x-kirichess-transport", document.Info.Description, StringComparison.Ordinal);
    }

    private static HashSet<string> HubPayloadNames(string hub) =>
        HubContract.ServerEvents.Where(e => e.Hub == hub).Select(e => e.Payload)
            .Concat(HubContract.ClientMethods.Where(m => m.Hub == hub).Select(m => m.Result))
            .Concat(HubContract.NestedPayloadTypes.Where(n => n.Hub == hub).Select(n => (Type?)n.Type))
            .OfType<Type>()
            .Select(type => type.Name)
            .ToHashSet();

    [Fact]
    public async Task Public_document_declares_auth_and_a_server()
    {
        // M-6-5. Importing the published document into Postman or a generator used to produce a
        // client with no base URL and no auth, and the description pointed at a repo path the
        // importer may not have. Every /api/v1 operation requires a kirichess_pat_ token, so the requirement
        // is document-level.
        var document = await GetDocumentAsync("public");

        Assert.Contains(document.Servers!, server => server.Url == "https://api.kirichess.com");
        Assert.Contains("bearerAuth", document.Components!.SecuritySchemes!.Keys);
        Assert.NotNull(document.Security);
        Assert.NotEmpty(document.Security);
        Assert.Contains("https://kirichess.com/developers", document.Info.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/api/openapi-public.json", document.Info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_declares_the_kill_switch_503()
    {
        var document = await GetDocumentAsync();
        var register = document.Paths!["/api/auth/register"].Operations!;
        Assert.Contains(register, pair =>
            pair.Key.ToString()!.Equals("Post", StringComparison.OrdinalIgnoreCase)
            && pair.Value.Responses!.ContainsKey("503"));
    }

    [Fact]
    public async Task Public_document_contains_only_v1_paths_and_no_moderation()
    {
        var document = await GetDocumentAsync("public");
        Assert.NotNull(document.Paths);
        Assert.NotEmpty(document.Paths);

        Assert.All(document.Paths.Keys, path =>
            Assert.StartsWith("/api/v1/", path, StringComparison.Ordinal));

        Assert.DoesNotContain(document.Paths.Keys, path =>
            path.StartsWith("/api/moderation", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Paths.Keys, path =>
            path.Equals("/api/account/delete", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/account/delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task First_party_document_excludes_v1_paths()
    {
        var document = await GetDocumentAsync();
        Assert.DoesNotContain(document.Paths!.Keys, path =>
            path.StartsWith("/api/v1/", StringComparison.Ordinal));
        Assert.Contains("/api/account/api-tokens", document.Paths.Keys);
    }

    [Fact]
    public async Task Production_serves_the_public_document_at_openapi_v1()
    {
        using var client = _factory!.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/moderation", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dto_schemas_mark_their_properties_required()
    {
        // Positional C# records carry no `required` keyword on their constructor parameters, so this
        // is not automatic - if System.Text.Json's JsonSchemaExporter (what AddOpenApi's document
        // generator uses internally) ever stopped marking them required, every property in every
        // generated schema would arrive in TypeScript as optional (`fen?: string`) and the whole
        // generated client (Task B) would be useless. Confirmed empirically that JsonSchemaExporter
        // already does this correctly for this codebase's DTOs with no extra transformer needed - this
        // fact pins that behaviour so a future refactor or package bump that silently loses it is a red
        // test, not a quiet regression Task B has to debug.
        var document = await GetDocumentAsync();
        var gameState = document.Components!.Schemas!["GameStateResponse"];

        Assert.NotNull(gameState.Required);
        Assert.Equal(
            ["gameId", "fen", "sideToMove", "whiteTime", "blackTime", "status", "winner", "termination",
             "whitePlayerId", "blackPlayerId", "moveCount", "economy", "rated", "activeClockOffsetMs"],
            gameState.Required!.ToArray());
    }

    private static void AssertSuccessResponses(OpenApiDocument document)
    {
        // Every gap is collected and reported together rather than asserted inside the loop. With a
        // fail-fast assertion, annotating a newly added endpoint set costs one full boot-and-generate
        // cycle PER MISSING ROUTE - the guard tells you about the second one only after you have fixed
        // the first. The whole point of a coverage guard is to hand you the list.
        var gaps = new List<string>();

        foreach (var (path, item) in document.Paths!)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                var httpMethod = method.ToString().ToUpperInvariant();
                var responses = operation.Responses!;

                if (!responses.Keys.Any(code => code.Length == 3 && code[0] is '2' or '3'))
                {
                    gaps.Add($"{httpMethod} {path} declares no 2xx/3xx response at all.");
                    continue;
                }

                if (NoJsonBodyAllowlist.Contains((httpMethod, path)))
                    continue;

                var has200Or201WithSchema = responses
                    .Where(r => r.Key is "200" or "201")
                    .Any(r => r.Value.Content is { } content &&
                              content.TryGetValue("application/json", out var mediaType) &&
                              mediaType.Schema is not null);

                if (!has200Or201WithSchema)
                    gaps.Add($"{httpMethod} {path} has no 200/201 response with an application/json schema.");
            }
        }

        Assert.True(gaps.Count == 0,
            $"{gaps.Count} operation(s) describe no success response. Add .Produces<T>(...) to each "
            + "Map* call, or add it to NoJsonBodyAllowlist with a reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, gaps.Order()));
    }

    private async Task<OpenApiDocument> GetDocumentAsync(string documentName = "v1")
    {
        var documentProvider = _factory!.Services
            .GetRequiredKeyedService<Microsoft.AspNetCore.OpenApi.IOpenApiDocumentProvider>(documentName);
        return await documentProvider.GetOpenApiDocumentAsync();
    }
}
