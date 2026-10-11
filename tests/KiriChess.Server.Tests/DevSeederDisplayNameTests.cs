using KiriChess.Server.Moderation.Content;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8a.4 added a unique index on ApplicationUser.NormalizedDisplayName. Data/DevSeeder.cs
/// de-duplicates its 45 generated demo names on the RAW string via a HashSet, which is no longer the
/// same thing as de-duplicating on the identity key (see Phase 8a.4 scope
/// decision 10 and TASK-A step 7's "expected fallout" note).
///
/// This reproduces DevSeeder's exact name-generation algorithm (same prefix/suffix tables, same seed)
/// without touching DevSeeder itself, and proves the 45 names it currently produces still key to 45
/// distinct NormalizedDisplayName values, so the seeder keeps running clean under the new index. Pure
/// and synchronous - no Docker, no database.
/// </summary>
public sealed class DevSeederDisplayNameTests
{
    // Mirrors Data/DevSeeder.cs's NamePrefixes/NameSuffixes/loop verbatim. If DevSeeder's tables or
    // loop ever change, update this copy too - it is deliberately NOT sharing code with DevSeeder so
    // that a change to the generator is forced to update its own uniqueness proof rather than
    // silently inheriting this test's result.
    private static readonly string[] NamePrefixes =
    {
        "Rook", "Knight", "Bishop", "Pawn", "Zugzwang", "Fianchetto", "Gambit",
        "Tempo", "Fork", "Skewer", "Endgame", "Flank", "Zwischenzug", "Prophylaxis",
        "Blitz", "Sac", "Outpost", "Overload", "Discover", "Waiting",
    };

    private static readonly string[] NameSuffixes =
    {
        "Master", "Hunter", "Storm", "Wizard", "Ghost", "Ranger", "Runner",
        "Slayer", "Sage", "Rider", "Falcon", "Wolf", "Phantom", "Ace", "Fox", "Owl",
    };

    private static List<string> GenerateDevSeederNames()
    {
        var rng = new Random(42);
        var usedNames = new HashSet<string>();
        var names = new List<string>();

        while (names.Count < 45)
        {
            var name = $"{NamePrefixes[rng.Next(NamePrefixes.Length)]}" +
                       $"{NameSuffixes[rng.Next(NameSuffixes.Length)]}{rng.Next(1, 999)}";
            if (!usedNames.Add(name)) continue;
            names.Add(name);
        }

        return names;
    }

    [Fact]
    public void DevSeeder_Names_Key_To_45_Distinct_NormalizedDisplayNames()
    {
        var names = GenerateDevSeederNames();
        Assert.Equal(45, names.Count);

        var identityKeys = names.Select(DisplayNameNormalizer.NormalizeForIdentity).ToArray();

        Assert.Equal(45, identityKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identityKeys, key => Assert.True(key.Length > 0));
    }
}
