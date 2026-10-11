using System.Text.RegularExpressions;
using KiriChess.Engine.Config;
using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// A pool carries two strings and they are not interchangeable. <c>key</c> is a frozen storage id:
/// it is the value in <c>Games.RatingPool</c>, <c>Ratings.Pool</c>, <c>RatingHistory.Pool</c> and
/// <c>Challenge.Pool</c>, it forms the Redis matchmaking queue keys and the <c>bot:{key}</c> rating
/// namespace, and it is the <c>preset</c> value the public API accepts. <c>label</c> is the display
/// name and the only place a pool's name is written.
///
/// That split only holds while nothing downstream hardcodes the word. Before this guard the labels
/// were duplicated into six <c>title:</c> literals on the rules page and twenty-five sentences in
/// <c>ui/messages/en.json</c>, which is what made "rename a pool" a repo-wide edit instead of a
/// config change. The prose now takes the name as an ICU placeholder (<c>{steal1}</c>) filled from
/// <c>POOL_NAMES</c>, so this asserts the labels stay absent from ui/ outside the generated file.
///
/// If this fails, the fix is to read the label from <c>POOL_NAMES</c> / <c>PRESETS[key].label</c> or
/// to add a <c>{key}</c> placeholder, never to add the file to the allowlist. The allowlist exists
/// for the one real homograph: <c>Flagfall</c> is also the termination reason
/// (<c>TerminationReason.Flagfall</c>, "your clock reached zero"), which every pool shares and which
/// does not move when the pool is renamed.
/// </summary>
public sealed class PoolLabelDriftTests
{
    /// <summary>Generated from the config; the one file allowed to contain the words.</summary>
    private const string GeneratedRules = "rules.generated.ts";

    /// <summary>
    /// Identifiers that spell a pool label but mean the termination reason: the <c>Flagfall</c>
    /// member of the reason enum and the <c>flagFallLoses</c> rule flag, in their various casings.
    /// Matched and removed before the label scan so the homograph does not produce a false failure.
    /// </summary>
    private static readonly Regex TerminationHomograph =
        new(@"[Ff]lag ?[Ff]all(Loses|Detected|Monitor|Sweep)?", RegexOptions.Compiled);

    private static IEnumerable<string> ScannedFiles(string root)
    {
        var roots = new[]
        {
            Path.Combine(root, "ui", "app"),
            Path.Combine(root, "ui", "components"),
            Path.Combine(root, "ui", "lib"),
            Path.Combine(root, "ui", "messages"),
            // Playwright selectors match on rendered button text, so a hardcoded label here is not a
            // cosmetic staleness: it is a red end-to-end run on the next rebrand.
            Path.Combine(root, "e2e", "tests"),
        };
        foreach (var path in roots)
        {
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name == GeneratedRules || name == "api.generated.ts") continue;
                if (Path.GetExtension(file) is ".ts" or ".tsx" or ".json") yield return file;
            }
        }
    }

    [Fact]
    public void PoolLabels_AreNotHardcodedAnywhereInTheUi()
    {
        var root = OpenApiExport.FindRepoRoot();
        var labels = RulesDocument.Instance.Pools.Select(p => p.Label).ToArray();

        // Lower bound: a scan that silently stops finding files is a test that passes for the wrong
        // reason, and a config that stops exposing labels would make every assertion below vacuous.
        Assert.Equal(6, labels.Length);
        var files = ScannedFiles(root).ToList();
        Assert.InRange(files.Count, 20, int.MaxValue);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = TerminationHomograph.Replace(File.ReadAllText(file), "");
            foreach (var label in labels)
            {
                if (!Regex.IsMatch(text, $@"\b{Regex.Escape(label)}\b")) continue;
                offenders.Add($"{Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, (char)47)}: {label}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Pool labels are owned by config/kirichess.rules.json and must reach the UI through "
                + "POOL_NAMES / PRESETS[key].label, or as a {key} ICU placeholder in a message. "
                + "Hardcoded here:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PoolKeys_AreStableStorageIds_NotDerivedFromLabels()
    {
        // The keys are written into four tables and a Redis keyspace, so they must survive a rename.
        // They are deliberately neutral ids rather than the pool names: a key that spells a name goes
        // stale the moment the name changes, and a stale name is worse than an opaque id because it
        // still reads as meaningful. `bounty1` says which economy the pool runs and nothing else, so
        // no rebrand can ever make it wrong. Asserting the exact set is what makes an accidental key
        // rename fail here, loudly, rather than silently orphaning every historical Glicko row.
        //
        // ICU note: these must stay free of hyphens. They double as placeholder names in
        // messages/en.json (`{steal1}`), and a hyphen is ICU Pattern_Syntax, so `steal-1` would not
        // parse as an argument.
        Assert.Equal(
            new[] { "bounty1", "burn1", "steal2", "steal1", "burn2", "steal3" },
            KiriChessConfig.RatedPoolKeys.ToArray());
    }
}
