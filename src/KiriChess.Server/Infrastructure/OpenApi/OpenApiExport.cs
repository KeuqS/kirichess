using System.Text;
using Microsoft.AspNetCore.OpenApi;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Offline OpenAPI export (Phase 9d.7). <c>dotnet run --project src/KiriChess.Server --
/// --export-openapi docs/api/openapi.json</c> writes the first-party document and its public sibling
/// and exits - see the comment block around <c>--export-openapi</c> in Program.cs for the full "why",
/// and Phase 9f.5 for why two documents exist.
/// </summary>
public static class OpenApiExport
{
    /// <summary>
    /// Configuration overlay that makes the exported document reproducible on any machine - it must
    /// not depend on whether the developer running it happens to have a database or a Redis configured
    /// locally. See Program.cs for the per-key rationale.
    /// </summary>
    public static readonly Dictionary<string, string?> DeterministicConfiguration = new()
    {
        ["Jwt:Key"] = "openapi-export-placeholder-key-at-least-32-bytes-long",
        ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
        ["RunMigrationsOnStartup"] = "false",
        ["SeedDemoData"] = "false",
        ["Retention:Enabled"] = "false",
        ["Redis:Configuration"] = "",
        ["Cors:AllowedOrigins:0"] = "https://openapi-export.invalid",
        // Port 0 asks Kestrel for any free ephemeral port. Confirmed empirically: the ApiExplorer data
        // that AddOpenApi's document generator reads from is only realized once the host has actually
        // started (WriteAsync calls app.StartAsync() below) - a bare `new WebApplication()` that never
        // starts reports zero endpoints and the document comes back with an empty paths object. Since
        // this mode really does start Kestrel, it must not fight a real `dotnet run` dev server (or a
        // second concurrent export) for launchSettings.json's fixed :5053/:7161.
        ["Urls"] = "http://127.0.0.1:0",
    };

    /// <summary>
    /// Writes the default "v1" document to <paramref name="path"/> and the public document to the
    /// sibling <c>*-public.json</c> (e.g. <c>docs/api/openapi.json</c> and
    /// <c>docs/api/openapi-public.json</c>), both normalized to LF line endings, exactly one trailing
    /// newline, and UTF-8 without a BOM.
    /// </summary>
    public static async Task WriteAsync(WebApplication app, string path)
    {
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(FindRepoRoot(), path);
        await WriteDocumentAsync(app.Services, "v1", resolvedPath);
        await WriteDocumentAsync(app.Services, "public", PublicSiblingPath(resolvedPath));
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> (the build output directory, e.g.
    /// <c>src/KiriChess.Server/bin/Release/net10.0/</c>) until a directory containing
    /// <c>KiriChess.slnx</c> is found. Shared by the export-mode write above and by
    /// <c>OpenApiContractTests</c>, so both agree on where the committed document lives regardless of
    /// which directory the process was actually launched from.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KiriChess.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate KiriChess.slnx by walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// The one code path that turns the app's route table into OpenAPI JSON, shared by the export mode
    /// above and by <c>OpenApiContractTests.Committed_document_is_up_to_date</c> - that sharing is what
    /// makes the freshness check meaningful rather than a comparison of two different serializers.
    /// </summary>
    public static async Task<string> SerializeAsync(IServiceProvider services, string documentName = "v1")
    {
        var documentProvider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(documentName);
        var document = await documentProvider.GetOpenApiDocumentAsync();

        var writer = new StringWriter();
        document.SerializeAsV31(new Microsoft.OpenApi.OpenApiJsonWriter(writer));

        var normalized = writer.ToString().Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        return normalized;
    }

    public static string PublicSiblingPath(string resolvedPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(resolvedPath))
            ?? throw new InvalidOperationException($"Could not resolve a directory for {resolvedPath}.");
        var name = Path.GetFileNameWithoutExtension(resolvedPath);
        var ext = Path.GetExtension(resolvedPath);
        return Path.Combine(directory, $"{name}-public{ext}");
    }

    private static async Task WriteDocumentAsync(IServiceProvider services, string documentName, string resolvedPath)
    {
        var json = await SerializeAsync(services, documentName);
        var directory = Path.GetDirectoryName(Path.GetFullPath(resolvedPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
        await File.WriteAllTextAsync(resolvedPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
