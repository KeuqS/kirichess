using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.OpenApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Guards the SignalR hub contract declared in <see cref="HubContract"/> (Phase 9e.9): that the
/// document carries a schema for every declared payload, that the schema agrees with what SignalR's
/// own serializer actually sends, and that nothing on either side of the hub connection, a server
/// <c>SendAsync</c>/<c>InvokeAsync</c> call, a client <c>conn.on</c>/<c>conn.invoke</c> call, or a
/// public method on <see cref="GameHub"/>/<see cref="PresenceHub"/>, names something
/// <see cref="HubContract"/> does not know about.
///
/// A new file, not additions to <see cref="OpenApiContractTests"/> (see Phase 9e.9,
/// step A7): that file is being edited by other work in flight, and a new file merges cleanly where an
/// edit to a shared one would not.
///
/// Boots the app the same Docker-free way <see cref="OpenApiContractTests"/> does, deliberately
/// duplicated rather than shared for the same reason, one boot helper touching both files would be the
/// merge conflict this split exists to avoid.
/// </summary>
public sealed class HubContractTests : IAsyncLifetime
{
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
    public async Task Every_declared_payload_has_a_component_schema()
    {
        var document = await GetDocumentAsync();
        var schemas = document.Components!.Schemas!;

        var missing = HubContract.PayloadTypes
            .Where(type => !schemas.ContainsKey(type.Name))
            .Select(type => type.Name)
            .Order()
            .ToArray();

        Assert.True(missing.Length == 0,
            $"{missing.Length} HubContract.PayloadTypes entr(y/ies) have no components.schemas entry: "
            + string.Join(", ", missing)
            + ". HubPayloadDocumentTransformer should have added one for every declared payload type.");
    }

    /// <summary>
    /// The test scope decision 3 (phase README) is about: the schema in the document comes from minimal
    /// APIs' <c>JsonOptions</c>, but what actually goes down the wire is produced by SignalR's own
    /// <c>AddJsonProtocol</c> payload options. They agree today, both camelCase, and they agree by
    /// configuration (<c>Program.cs</c>'s <c>AddJsonProtocol</c> line), not by construction, so this
    /// walks the hub's <em>real</em> serializer options and compares what it would actually name each
    /// property against what the document claims.
    /// </summary>
    [Fact]
    public async Task Hub_schemas_match_what_SignalRs_serializer_actually_sends()
    {
        var document = await GetDocumentAsync();
        var schemas = document.Components!.Schemas!;

        var payloadOptions = _factory!.Services
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>()
            .Value.PayloadSerializerOptions;

        // The naming policy is the property under test below, so it is pinned here directly: if a
        // future edit to Program.cs's AddJsonProtocol line ever dropped CamelCase, this assertion fails
        // right here with a message that points at that line, instead of surfacing eleven confusing
        // property-name diffs from the loop further down.
        Assert.Same(JsonNamingPolicy.CamelCase, payloadOptions.PropertyNamingPolicy);

        // payloadOptions.TypeInfoResolver can be null until SignalR has actually serialized something
        // on this factory (confirmed empirically: GetTypeInfo throws InvalidOperationException,
        // "no resolver configured", without this), so the options are copied rather than reused, and
        // the resolver is filled in on the copy. Re-asserting the naming policy on the copy is the
        // verification step A7 asks for: it confirms the JsonSerializerOptions copy constructor really
        // does carry PropertyNamingPolicy across, so this test cannot pass vacuously against a copy that
        // silently reset to the default (PascalCase, unchanged) naming.
        var options = new JsonSerializerOptions(payloadOptions);
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        Assert.Same(JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);

        var mismatches = new List<string>();
        var visited = new HashSet<Type>();

        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            // A type missing from the document is Every_declared_payload_has_a_component_schema's
            // finding to report, not this test's; walking further into it here would either throw or
            // pile a second, less specific complaint about the same root cause on top of the first.
            if (!schemas.TryGetValue(type.Name, out var schema)) return;

            var typeInfo = options.GetTypeInfo(type);
            var wireNames = typeInfo.Properties.Select(p => p.Name).ToHashSet();
            // Empirically confirmed (Phase 9e.9): an OpenApiSchemaReference built with
            // `new OpenApiSchemaReference(id, document)` does not proxy .Properties (or even .Target)
            // to the schema it names, both come back null unless the document has gone through a full
            // load/resolve pass, which this in-memory, just-built document never does. So every type is
            // resolved here by id straight out of document.Components.Schemas rather than by following
            // a reference's own .Properties, which is why Visit is called with a CLR Type, never with
            // a schema object read off some other schema's Properties dictionary.
            var documentNames = schema.Properties?.Keys.ToHashSet() ?? [];

            var onlyInSignalR = wireNames.Except(documentNames).Order().ToArray();
            var onlyInDocument = documentNames.Except(wireNames).Order().ToArray();
            if (onlyInSignalR.Length > 0 || onlyInDocument.Length > 0)
            {
                mismatches.Add(
                    $"{type.Name}: SignalR sends [{string.Join(",", wireNames.Order())}], the document "
                    + $"declares [{string.Join(",", documentNames.Order())}]"
                    + (onlyInSignalR.Length > 0 ? $"; missing from the document: {string.Join(",", onlyInSignalR)}" : "")
                    + (onlyInDocument.Length > 0 ? $"; missing from what SignalR sends: {string.Join(",", onlyInDocument)}" : ""));
            }

            // Recurse into every composed type reachable from this one's own properties (or list
            // properties), the same shape HubPayloadDocumentTransformer.SubstituteReferences walks, so
            // GameStateResponse and ClockEventDto get checked too even though neither is itself a
            // HubContract.PayloadTypes entry.
            foreach (var property in type.GetProperties())
            {
                var propertyType = property.PropertyType;
                if (propertyType.IsGenericType && propertyType.GetGenericArguments() is [var elementType])
                    propertyType = elementType;

                if (propertyType.Namespace == typeof(GameSnapshotDto).Namespace && propertyType.IsClass)
                    Visit(propertyType);
            }
        }

        foreach (var type in HubContract.PayloadTypes)
            Visit(type);

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} hub payload type(s) disagree between what SignalR's own serializer "
            + "sends and what the document declares:"
            + Environment.NewLine + string.Join(Environment.NewLine, mismatches));
    }

    // Matches `.SendAsync("Name"` and `.InvokeAsync<T>("Name"` (LatencyProbe.cs's Ping is the only
    // InvokeAsync<T> hub call today, see HubContract.cs's comment on it). Requiring a leading dot and a
    // quoted first argument is what excludes every other SendAsync in this codebase, HttpClient's,
    // EmailMessage's, GameCommandRouter's, none of which is a hub call and none of which has a quoted
    // string literal as its first argument.
    private static readonly Regex ServerCallRegex = new(
        """\.(?:SendAsync|InvokeAsync(?:<[^>]*>)?)\(\s*"([A-Za-z][A-Za-z0-9]*)["]""",
        RegexOptions.Compiled);

    [Fact]
    public void Every_event_the_server_sends_is_declared()
    {
        var repoRoot = OpenApiExport.FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var declaredNames = HubContract.ServerEvents.Select(e => e.Name).ToHashSet();

        var offenders = new List<string>();
        var sitesFound = 0;

        foreach (var file in EnumerateSourceFiles(srcRoot, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ServerCallRegex.Matches(text))
            {
                sitesFound++;
                var name = match.Groups[1].Value;
                if (!declaredNames.Contains(name))
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{line} sends \"{name}\", which is not in HubContract.ServerEvents.");
                }
            }
        }

        // A regex over source text is fragile by nature, this lower bound is the guard against the
        // regex itself silently breaking (a refactor to raw-string call syntax, say) and this test then
        // passing for the wrong reason, zero call sites found. Fourteen call sites is the count the
        // phase README recorded when this was written; the real count found here may run higher as the
        // codebase grows, never near zero.
        Assert.True(sitesFound >= 10,
            $"Only found {sitesFound} SendAsync/InvokeAsync call site(s) with a quoted event name under "
            + "src/. That is far fewer than expected, which means ServerCallRegex stopped matching real "
            + "call sites rather than the codebase having genuinely shrunk its hub surface. A zero (or "
            + "near-zero) match count here is a broken test, not a clean codebase.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} server-side hub call(s) name an event HubContract.ServerEvents does not "
            + "declare:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // Matches `.on("Name"` and `.invoke("Name"` in ui/. Both SignalR client method names, `conn.on` for
    // a server event handler and `conn.invoke`/`connRef.current?.invoke` for a client-callable method,
    // confirmed empirically against every current call site that no non-hub `.on(`/`.invoke(` call in
    // ui/ takes a quoted string first argument, so this needs no further disambiguation.
    private static readonly Regex ClientOnRegex = new("""\.on\(\s*"([A-Za-z][A-Za-z0-9]*)["]""", RegexOptions.Compiled);
    private static readonly Regex ClientInvokeRegex = new("""\.invoke\(\s*"([A-Za-z][A-Za-z0-9]*)["]""", RegexOptions.Compiled);

    [Fact]
    public void Every_event_and_method_the_client_uses_is_declared()
    {
        var repoRoot = OpenApiExport.FindRepoRoot();
        var uiRoot = Path.Combine(repoRoot, "ui");
        var declaredEvents = HubContract.ServerEvents.Select(e => e.Name).ToHashSet();
        var declaredMethods = HubContract.ClientMethods.Select(m => m.Name).ToHashSet();

        var offenders = new List<string>();
        var sitesFound = 0;

        var files = EnumerateSourceFiles(uiRoot, "*.ts")
            .Concat(EnumerateSourceFiles(uiRoot, "*.tsx"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}.next{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);

            foreach (Match match in ClientOnRegex.Matches(text))
            {
                sitesFound++;
                var name = match.Groups[1].Value;
                if (!declaredEvents.Contains(name))
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{line} listens for \"{name}\", which is not in HubContract.ServerEvents.");
                }
            }

            foreach (Match match in ClientInvokeRegex.Matches(text))
            {
                sitesFound++;
                var name = match.Groups[1].Value;
                if (!declaredMethods.Contains(name))
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{line} invokes \"{name}\", which is not in HubContract.ClientMethods.");
                }
            }
        }

        Assert.True(sitesFound >= 10,
            $"Only found {sitesFound} conn.on/conn.invoke call site(s) with a quoted name under ui/. "
            + "That is far fewer than expected, which means the regexes stopped matching real call "
            + "sites rather than the codebase having genuinely shrunk its hub usage. A zero (or "
            + "near-zero) match count here is a broken test, not a clean codebase.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} client-side hub call(s) name an event or method HubContract does not "
            + "declare:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Every_public_hub_method_is_declared()
    {
        var offenders = new List<string>();

        CheckHub(typeof(GameHub), HubContract.GameHub, offenders);
        CheckHub(typeof(PresenceHub), HubContract.PresenceHub, offenders);

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} public hub method(s) are not declared in HubContract.ClientMethods, or "
            + "declare the wrong result type:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static void CheckHub(Type hubType, string hub, List<string> offenders)
    {
        var declared = HubContract.ClientMethods.Where(m => m.Hub == hub).ToDictionary(m => m.Name);

        // Public instance methods declared directly on the hub class. DeclaringType == hubType already
        // excludes everything merely inherited from Hub (Dispose, and the like); the extra
        // GetBaseDefinition check is what is needed for OnConnectedAsync/OnDisconnectedAsync, which are
        // redeclared (overridden) on the derived class and so pass the DeclaringType filter on their
        // own, they are lifecycle hooks Hub itself defines, not client-callable methods this contract
        // should ever list.
        var methods = hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.DeclaringType == hubType)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetBaseDefinition().DeclaringType == m.DeclaringType);

        foreach (var method in methods)
        {
            if (!declared.TryGetValue(method.Name, out var contractMethod))
            {
                offenders.Add($"{hubType.Name}.{method.Name} is public but not declared in HubContract.ClientMethods for {hub}.");
                continue;
            }

            var actualResult = UnwrapResult(method);
            if (actualResult != contractMethod.Result)
            {
                offenders.Add(
                    $"{hubType.Name}.{method.Name} returns "
                    + $"{(actualResult is null ? "no result" : actualResult.Name)}, but HubContract.ClientMethods "
                    + $"declares {(contractMethod.Result is null ? "no result" : contractMethod.Result.Name)}.");
            }
        }
    }

    private static Type? UnwrapResult(MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (returnType == typeof(Task))
            return null;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return returnType.GetGenericArguments()[0];
        return returnType;
    }

    /// <summary>
    /// The walk has to tolerate what the filters below would have discarded anyway. A running
    /// <c>next dev</c> leaves directory symlinks under <c>ui/.next/dev/node_modules/</c> pointing back
    /// into <c>ui/node_modules/</c>, and <see cref="SearchOption.AllDirectories"/> throws an
    /// IOException the moment it tries to open one, mid-enumeration, before any <c>.Where</c> can
    /// exclude it. That turned this contract test into a failure that looks like a wire-surface
    /// break but is really just "the dev server is running", so the skipping belongs in the
    /// enumeration itself, not downstream of it. Skipping reparse points also stops the scan
    /// following a symlink back into node_modules and reading the same tree twice.
    /// </summary>
    private static readonly EnumerationOptions SourceScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private static IEnumerable<string> EnumerateSourceFiles(string root, string searchPattern) =>
        Directory.EnumerateFiles(root, searchPattern, SourceScanOptions)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private async Task<OpenApiDocument> GetDocumentAsync()
    {
        var documentProvider = _factory!.Services
            .GetRequiredKeyedService<Microsoft.AspNetCore.OpenApi.IOpenApiDocumentProvider>("v1");
        return await documentProvider.GetOpenApiDocumentAsync();
    }
}
