using KiriChess.Engine.Tooling;

namespace KiriChess.Engine.Tests;

/// <summary>
/// Guards <c>config/cost-vectors.generated.json</c> (Phase 10b.1): the committed golden
/// (position, move) -&gt; economy vectors <c>ui/lib/costPreview.test.ts</c> replays
/// <c>computeCostPreview</c> against. Regenerates the file in-process from the exact same
/// <see cref="CostVectorGenerator"/> the <c>--export-cost-vectors</c> CLI mode calls (the same sharing
/// <c>OpenApiContractTests</c> uses for the OpenAPI document) and byte-compares, so a change to
/// <c>KiriChessEngine.ApplyEconomy</c> that isn't matched by a regeneration fails here, rather than
/// silently drifting from what the TypeScript mirror is being checked against.
/// </summary>
public sealed class CostVectorConformanceTests
{
    [Fact]
    public void Committed_vectors_are_up_to_date()
    {
        var generated = CostVectorGenerator.Serialize(CostVectorGenerator.GenerateVectors());

        var repoRoot = CostVectorGenerator.FindRepoRoot();
        var committedPath = Path.Combine(repoRoot, "config", "cost-vectors.generated.json");
        Assert.True(File.Exists(committedPath),
            $"{committedPath} does not exist. Generate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-cost-vectors config/cost-vectors.generated.json");

        // Normalize \r\n to \n before comparing: a Windows checkout can carry CRLF line endings even
        // though the file was written LF-only - OpenApiContractTests applies the same normalization
        // for the same reason.
        var committed = File.ReadAllText(committedPath).Replace("\r\n", "\n");

        Assert.True(string.Equals(generated, committed, StringComparison.Ordinal),
            "config/cost-vectors.generated.json is stale. Regenerate it: " +
            "dotnet run --project src/KiriChess.Server -- --export-cost-vectors config/cost-vectors.generated.json");
    }
}
