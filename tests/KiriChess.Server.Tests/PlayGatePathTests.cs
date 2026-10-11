using System.Text.RegularExpressions;
using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The source-scan half of the confirmed-email play gate's guard
/// (<c>Auth:RequireConfirmedEmailForPlayFrom</c>), in the shape <see cref="TokenMintingPathTests"/>
/// established for exactly this problem and for the same stated reason: the gate is hand-applied per
/// entry point, so fixing the paths that exist does not close the class of miss. A future path that
/// seats a player without consulting the policy is the bug, and this is what fails on it.
///
/// Deliberately holds no fixture. <see cref="PlayGateTests"/> is the behavioural half and owns the
/// Postgres container; keeping the scan separate means it runs in milliseconds without Docker, which
/// is the property that makes it worth having at all.
/// </summary>
public sealed class PlayGatePathTests
{
    private enum Gate
    {
        /// <summary>A gate call must appear before this site creates a game.</summary>
        Required,
        /// <summary>It deliberately does not, for the reason recorded beside it.</summary>
        NotApplicable
    }

    /// <summary>
    /// Every call site that creates a game, keyed by source file and the route literal or method name
    /// enclosing it. Adding a row is a deliberate act: the scan fails on any creating site missing
    /// from here, and on any row here that no longer matches a site.
    /// </summary>
    private static readonly Dictionary<(string File, string Entry), (Gate Check, string Why)> Declared = new()
    {
        [("MatchmakingService.cs", "TryJoinAsync")] =
            (Gate.Required, "Queue join, the rated pools. The gate runs ahead of the pairing loop below it."),

        [("GameEndpoints.cs", "/")] =
            (Gate.Required, "POST /api/games, the custom/solo game builder."),

        [("ChallengeEndpoints.cs", "AcceptChallengeAsync")] =
            (Gate.Required, "Challenge accept, shared by /api/challenges and /api/v1/challenges."),

        [("PublicApiEndpoints.cs", "/games")] =
            (Gate.Required,
             "POST /api/v1/games. An API token is minted by a signed-in user, so without this an "
             + "unconfirmed account could mint one and play the whole surface straight past the gate."),

        [("GameHub.cs", "AcceptRematch")] =
            (Gate.Required, "Rematch accept. Near-unreachable (both players cleared the gate to reach the "
                          + "finished game this offer hangs off) and kept as the belt to that braces."),
    };

    // GameRegistry.CreateAsync is the single place a game is born - every path above ends there, and a
    // new path that does not is not a game. The `_?` is load-bearing: `\b` does not sit between `_` and
    // `r`, so a pattern anchored on `registry` alone silently misses every `_registry` field call, which
    // is two of the five sites and is what the site-count floor below caught.
    private static readonly Regex CreateRegex = new(@"\b_?registry\.CreateAsync\(", RegexOptions.Compiled);
    private static readonly Regex GateRegex = new(@"\bEnsureEmailConfirmedAsync\(", RegexOptions.Compiled);

    private static readonly Regex RouteRegex = new(
        @"Map(?:Get|Post|Put|Patch|Delete)\(\s*""([^""]*)""", RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(
        @"(?:internal|public|private)\s+(?:static\s+)?async\s+Task[^\s(]*\s+(\w+)\(", RegexOptions.Compiled);

    /// <summary>
    /// The guard against <see cref="CreateRegex"/> quietly breaking and this whole file then passing
    /// because it matched nothing. Same reasoning and same shape as TokenMintingPathTests' own floor.
    /// </summary>
    private const int KnownCreateSites = 5;

    [Fact]
    public void Every_game_creating_call_site_is_declared()
    {
        var found = ScanCreateSites();

        Assert.True(found.Count >= KnownCreateSites,
            $"The scan found {found.Count} game-creating call sites, fewer than the {KnownCreateSites} "
            + "known to exist. CreateRegex has probably stopped matching; fix it rather than lowering "
            + "the floor, or this whole file passes by finding nothing.");

        var undeclared = found.Keys.Where(k => !Declared.ContainsKey(k)).ToArray();
        var stale = Declared.Keys.Where(k => !found.ContainsKey(k)).ToArray();

        Assert.True(undeclared.Length == 0,
            "These call sites create a game and are not declared in PlayGatePathTests.Declared:\n  "
            + string.Join("\n  ", undeclared.Select(k => $"{k.File} {k.Entry}"))
            + "\n\nAdd a row saying whether the path enforces the confirmed-email gate and why. A path "
            + "that seats a player and that nobody decided about is the bug this test exists to catch.");

        Assert.True(stale.Length == 0,
            "These declared sites no longer create a game; remove the rows:\n  "
            + string.Join("\n  ", stale.Select(k => $"{k.File} {k.Entry}")));
    }

    [Fact]
    public void Every_path_declared_gated_checks_before_it_creates()
    {
        var found = ScanCreateSites();

        var offenders = Declared
            .Where(d => d.Value.Check == Gate.Required)
            .Where(d => found.TryGetValue(d.Key, out var gatedFirst) && !gatedFirst)
            .Select(d => $"{d.Key.File} {d.Key.Entry}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These paths are declared Gate.Required but no EnsureEmailConfirmedAsync call appears "
            + "before the point where they create a game:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_move_path_is_deliberately_not_an_enforcement_point()
    {
        // The rule SanctionPolicy already states for sanctions: a rejected move can lose a game on
        // time, so a gate there would turn a configuration change into a flagfall. A future "fix" that
        // adds the gate to move submission is a regression, not a hardening.
        var source = ReadServerFile("Games", "GameEndpoints.cs");
        var start = source.IndexOf("MapPost(\"/{id:guid}/moves\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "GameEndpoints.cs no longer maps the move route; update this pin.");
        var next = source.IndexOf("group.Map", start + 1, StringComparison.Ordinal);
        var handler = next >= 0 ? source[start..next] : source[start..];
        Assert.DoesNotContain("EnsureEmailConfirmedAsync(", handler);
    }

    /// <summary>
    /// Each game-creating site as (file, enclosing route or method) -> whether a gate call appears
    /// earlier in the same file.
    /// </summary>
    private static Dictionary<(string File, string Entry), bool> ScanCreateSites()
    {
        var gamesRoot = Path.Combine(OpenApiExport.FindRepoRoot(), "src", "KiriChess.Server", "Games");
        var found = new Dictionary<(string, string), bool>();

        foreach (var file in Directory.EnumerateFiles(gamesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            var gates = GateRegex.Matches(text);

            foreach (Match create in CreateRegex.Matches(text))
            {
                var entry = EnclosingEntry(text, create.Index);
                if (entry is null) continue;

                var gatedFirst = gates.Any(g => g.Index < create.Index);

                // Two creating sites under one entry collapse to one key, and the verdict is OR-ed the
                // same way: the question this asks is whether the path checks, not how many times.
                found[(name, entry)] = found.TryGetValue((name, entry), out var prior)
                    ? prior || gatedFirst
                    : gatedFirst;
            }
        }

        return found;
    }

    /// <summary>The nearest route literal or method declaration above <paramref name="index"/>.</summary>
    private static string? EnclosingEntry(string text, int index)
    {
        var head = text[..index];

        var route = RouteRegex.Matches(head).LastOrDefault();
        var method = MethodRegex.Matches(head).LastOrDefault();

        if (route is null && method is null) return null;
        if (route is null) return method!.Groups[1].Value;
        if (method is null) return route.Groups[1].Value;

        return route.Index > method.Index ? route.Groups[1].Value : method.Groups[1].Value;
    }

    private static string ReadServerFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { OpenApiExport.FindRepoRoot(), "src", "KiriChess.Server" }.Concat(parts).ToArray()));
}
