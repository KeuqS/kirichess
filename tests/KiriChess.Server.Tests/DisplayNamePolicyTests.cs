using System.Text.Json;
using KiriChess.Server.Moderation.Content;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8a.4 pure policy surface from Phase 8a.4 and
/// TASK-A-content-policy-foundation.md: the two normalizers (identity vs matching, scope decision 1),
/// ContentPolicy's rule order, the ASCII-only charset rule (scope decision 9), the Scunthorpe
/// allowlist carve-out, and the "never echo a blocklist term" rule (scope decision 2). Pure and
/// synchronous, so this needs no Docker and no database - the BalanceAnalyticsTests.cs model.
/// </summary>
public sealed class DisplayNamePolicyTests
{
    private static readonly ContentModerationOptions DefaultOptions = new(MinLength: 3, MaxLength: 24, RenameCooldownDays: 30);

    /// <summary>Walks up from the test binary to the repo root (the folder holding KiriChess.slnx).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KiriChess.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string OnDiskConfigPath() =>
        Path.Combine(RepoRoot(), "config", "kirichess.moderation.json");

    // ---- Identity vs matching (scope decision 1) ----

    [Fact]
    public void Identity_And_Matching_Are_Different_Normalizations()
    {
        // Repeat-collapsing must never touch the identity key: "Anna" and "Ana" are two different
        // real people's real names, and the identity key must not treat them as the same account.
        Assert.NotEqual(DisplayNameNormalizer.NormalizeForIdentity("Anna"), DisplayNameNormalizer.NormalizeForIdentity("Ana"));

        // Matching tolerates that same repeat, because it exists to catch obfuscated profanity.
        Assert.Equal(DisplayNameNormalizer.NormalizeForMatching("fuuuuuck"), DisplayNameNormalizer.NormalizeForMatching("fuck"));
    }

    [Theory]
    [InlineData("B0b")]
    [InlineData("Bob")]
    [InlineData("B-o-b")]
    [InlineData("B_o_b")]
    [InlineData("b o b")]
    public void Leetspeak_And_Separators_Fold_To_The_Same_Identity_Key(string candidate)
    {
        Assert.Equal("bob", DisplayNameNormalizer.NormalizeForIdentity(candidate));
    }

    [Fact]
    public void Cyrillic_Homoglyph_Folds_To_The_Latin_Identity_Key()
    {
        // Cyrillic "о" (U+043E), not Latin "o".
        Assert.Equal("bob", DisplayNameNormalizer.NormalizeForIdentity("Bоb"));
    }

    [Fact]
    public void Fullwidth_Forms_Fold_Via_NFKD()
    {
        Assert.Equal("bob", DisplayNameNormalizer.NormalizeForIdentity("Ｂｏｂ")); // "Ｂｏｂ"
    }

    // ---- The '1' ambiguity fix-up ----
    //
    // '1' reads as both 'l' and 'i'. NormalizeForIdentity resolves it to 'l' - and that resolution is
    // pinned and does not change - because it is the better impersonation answer: "A1ice" must collide
    // with "Alice" in the unique index. IdentityVariants/MatchingVariants add the 'i' reading as an
    // ADDITIONAL read-time policy check, without touching what gets persisted.

    [Fact]
    public void Persisted_Identity_Is_Always_The_Canonical_L_Reading_Never_A_Variant()
    {
        // This is the load-bearing fact the fix-up rests on: NormalizeForIdentity itself is unchanged,
        // so "A1ice" and "Alice" still fold to the SAME persisted key ("alice", via '1'->'l') - the
        // exact impersonation collision the unique index exists to create. If a future change ever
        // makes these differ, the identity table was touched by mistake and the uniqueness guarantee
        // this phase is built around has quietly broken.
        Assert.Equal("alice", DisplayNameNormalizer.NormalizeForIdentity("A1ice"));
        Assert.Equal("alice", DisplayNameNormalizer.NormalizeForIdentity("Alice"));
        Assert.Equal(
            DisplayNameNormalizer.NormalizeForIdentity("A1ice"),
            DisplayNameNormalizer.NormalizeForIdentity("Alice"));
    }

    [Fact]
    public void IdentityVariants_Adds_The_I_Reading_Only_When_It_Differs_From_The_Canonical_Reading()
    {
        // "4dm1n": the canonical 'l' reading ("admln") and the 'i' reading ("admin") differ, so both
        // are returned, canonical first.
        Assert.Equal(["admln", "admin"], DisplayNameNormalizer.IdentityVariants("4dm1n"));

        // No '1' in the input at all: only the canonical reading, never a duplicate second entry.
        Assert.Equal(["bob"], DisplayNameNormalizer.IdentityVariants("Bob"));
    }

    [Theory]
    [InlineData("Delta1")]
    [InlineData("Player1")]
    [InlineData("Rook1")]
    public void Digit_Suffixed_Names_With_No_Real_Word_Reading_Are_Still_Allowed(string candidate)
    {
        // The '1'-as-'i' variant does not manufacture false positives: these are ordinary handles, and
        // neither reading of any of them matches a reserved or blocked term.
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);
        Assert.True(verdict.Allowed);
    }

    // ---- Substance ----

    [Fact]
    public void Emoji_And_Punctuation_Only_Candidate_Keys_To_Empty()
    {
        Assert.Equal(string.Empty, DisplayNameNormalizer.NormalizeForIdentity("🎉🎉🎉")); // "🎉🎉🎉"
    }

    [Fact]
    public void NotEnoughSubstance_Fires_When_The_Identity_Key_Is_Too_Short()
    {
        // Under the shipped default (MinLength: 3), a legal-charset candidate that clears the
        // separator-placement rule (rule 4) always has at least two alphanumeric characters at its
        // boundaries, so rule 5 (substance) can never actually be the reason a real signup is
        // refused - rule 3 (charset) or rule 4 (separator placement) always fires first. This test
        // exercises rule 5 directly with a MinLength of 1, which is the only way to reach it, to prove
        // the check itself is correct in isolation.
        var tinyMinLength = new ContentModerationOptions(MinLength: 1, MaxLength: 24, RenameCooldownDays: 30);
        var verdict = ContentPolicy.Default.CheckDisplayName("a", tinyMinLength);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.NotEnoughSubstance, verdict.Rejection);
    }

    [Fact]
    public void Punctuation_Only_Candidate_Is_Rejected_Under_Default_Options()
    {
        // Under the default options, a candidate made only of legal separators always starts and ends
        // with one, so it is caught by SeparatorPlacement (rule 4) before substance (rule 5) is ever
        // reached; a candidate made only of illegal symbols is caught by IllegalCharacter (rule 3)
        // instead. Both are documented alternatives to NotEnoughSubstance above.
        var separatorsOnly = ContentPolicy.Default.CheckDisplayName("...", DefaultOptions);
        Assert.False(separatorsOnly.Allowed);
        Assert.Equal(DisplayNameRejection.SeparatorPlacement, separatorsOnly.Rejection);

        var illegalOnly = ContentPolicy.Default.CheckDisplayName("🎉🎉🎉", DefaultOptions); // "🎉🎉🎉"
        Assert.False(illegalOnly.Allowed);
        Assert.Equal(DisplayNameRejection.IllegalCharacter, illegalOnly.Rejection);
    }

    // ---- Rule order ----

    [Fact]
    public void Long_Profane_String_Reports_TooLong_Not_Profanity()
    {
        var candidate = new string('f', 20) + "shit" + new string('f', 20); // 44 chars, well over MaxLength
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.TooLong, verdict.Rejection);
    }

    // ---- Charset (scope decision 9) ----

    [Theory]
    [InlineData("Bоb")]      // Cyrillic о
    [InlineData("Bob🎉")] // trailing emoji
    public void NonAscii_Candidate_Reports_IllegalCharacter(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.IllegalCharacter, verdict.Rejection);
    }

    // ---- Separator placement ----

    [Theory]
    [InlineData("-Bob")]
    [InlineData("Bob.")]
    [InlineData("Bo__b")]
    public void Bad_Separator_Placement_Is_Rejected(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.SeparatorPlacement, verdict.Rejection);
    }

    [Theory]
    [InlineData("Bo_b")]
    [InlineData("Bo-b")]
    [InlineData("Bo.b")]
    [InlineData("Bo b")]
    public void Single_Interior_Separator_Is_Allowed(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.True(verdict.Allowed);
    }

    // ---- Reserved ----

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("A d m i n")]
    [InlineData("4dmin")] // '4' folds to 'a' via the confusables table (Step 1)
    [InlineData("4dm1n")] // the plan's own example: only reachable via the '1'-as-'i' identity
                           // variant (IdentityVariants) - the canonical '1'-as-'l' reading alone
                           // folds this to "admln", which is not reserved. See the fix-up report.
    public void Reserved_Name_Is_Rejected_Regardless_Of_Casing_Spacing_Or_Leetspeak(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.Reserved, verdict.Rejection);
    }

    // ---- Profanity, with obfuscation ----

    [Theory]
    [InlineData("s h i t")]
    [InlineData("shiiiit")]
    [InlineData("c0ck")]  // '0' folds to 'o' via the confusables table (Step 1)
    [InlineData("sh1t")]  // only reachable via the '1'-as-'i' matching variant - the canonical
    [InlineData("b1tch")] // '1'-as-'l' reading alone folds these to "shlt"/"bltch", neither blocked
    public void Obfuscated_Profanity_Is_Rejected(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.Profanity, verdict.Rejection);
    }

    // ---- Scunthorpe ----

    [Theory]
    [InlineData("Scunthorpe")]
    [InlineData("Assassin")]
    [InlineData("Classic")]
    [InlineData("Cocktail")]
    [InlineData("Analysis")]
    public void Scunthorpe_Words_Are_Allowed(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Boundary_Evasion_Across_An_Allowed_Word_Is_Still_Blocked()
    {
        // "classhit" is not itself allow-listed and is not itself blocked verbatim; "shit" only
        // appears once "class" is stripped from the front. Stripping "class" as a first pass would
        // leave "hit" and miss it. The positional carve-out must still catch it.
        var policy = ContentPolicy.Parse("""
        {
          "schemaVersion": 1,
          "blockedSubstrings": ["shit"],
          "blockedExact": [],
          "reservedExact": [],
          "reservedSubstrings": [],
          "allowedSubstrings": ["class"]
        }
        """);

        var verdict = policy.CheckDisplayName("classhit", DefaultOptions);

        Assert.False(verdict.Allowed);
        Assert.Equal(DisplayNameRejection.Profanity, verdict.Rejection);
    }

    // ---- No term leakage (scope decision 2) ----

    [Fact]
    public void Rejection_Messages_Never_Echo_A_Blocklist_Term()
    {
        var candidates = new[]
        {
            "ab",                    // TooShort
            new string('a', 30),     // TooLong
            "Bob🎉",       // IllegalCharacter
            "-Bob",                  // SeparatorPlacement
            "admin",                 // Reserved
            "shit",                  // Profanity
        };

        var messages = candidates
            .Select(c => ContentPolicy.Default.CheckDisplayName(c, DefaultOptions).Message)
            .Where(m => m.Length > 0)
            .ToArray();

        Assert.NotEmpty(messages);

        foreach (var term in ShippedBlocklistTerms())
        foreach (var message in messages)
            Assert.DoesNotContain(term, message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only the profanity lists (blockedSubstrings/blockedExact), not reservedExact/reservedSubstrings:
    /// the reserved list deliberately includes generic short English words ("me", "you", "here", "api")
    /// that are expected to turn up as ordinary substrings inside plain English UI copy (e.g. "na-me-s"
    /// inside "names"). Scope decision 2 is about never revealing WHICH entry matched, not about
    /// scrubbing every common two-letter word from the product's copy - the actual slurs and obscenities
    /// in blockedSubstrings/blockedExact are the ones that must never leak.
    /// </summary>
    // ---- Regression: a term list must never be repeat-collapsed on load. ----
    //
    // This shipped once and passed 276 tests, because no test used a display name containing a "k".
    // Normalizing the term lists with NormalizeForMatching collapsed the blockedSubstrings entry
    // "kkk" to the single character "k", after which every name containing a k was refused as
    // profanity. Both tests below fail loudly if anyone reintroduces that.

    [Theory]
    [InlineData("Knight")]
    [InlineData("Kasparov")]
    [InlineData("Mike")]
    [InlineData("Rook1")]
    [InlineData("Blackburne")]
    public void Ordinary_Names_Containing_A_Repeated_Blocklist_Letter_Are_Allowed(string candidate)
    {
        var verdict = ContentPolicy.Default.CheckDisplayName(candidate, DefaultOptions);
        Assert.True(verdict.Allowed, $"'{candidate}' was refused as {verdict.Rejection}");
    }

    /// <summary>
    /// The structural guard, and the one that actually prevents recurrence: no normalized term may
    /// collapse to something short enough to match half the alphabet. A single-character entry in a
    /// containment list refuses every name containing that letter.
    /// </summary>
    [Fact]
    public void No_Normalized_Blocklist_Term_Is_Short_Enough_To_Match_Arbitrary_Names()
    {
        foreach (var term in ShippedBlocklistTerms())
        {
            var normalized = DisplayNameNormalizer.NormalizeForIdentity(term);
            Assert.True(normalized.Length >= 3,
                $"Blocklist term '{term}' normalizes to '{normalized}' ({normalized.Length} chars). " +
                "A term this short matches ordinary names. Term lists must never be repeat-collapsed.");
        }
    }

    /// <summary>A term carrying an intentional repeat still matches - it is caught on the uncollapsed key.</summary>
    [Fact]
    public void A_Term_With_Intentional_Repeats_Still_Matches()
    {
        Assert.False(ContentPolicy.Default.CheckDisplayName("KKK", DefaultOptions).Allowed);
        Assert.False(ContentPolicy.Default.CheckDisplayName("kkk fan", DefaultOptions).Allowed);
    }

    /// <summary>And padding a term still matches - that one is caught on the collapsed key.</summary>
    [Fact]
    public void A_Padded_Candidate_Still_Matches()
    {
        Assert.False(ContentPolicy.Default.CheckDisplayName("fuuuuck", DefaultOptions).Allowed);
        Assert.False(ContentPolicy.Default.CheckDisplayName("shiiit", DefaultOptions).Allowed);
    }

    private static IEnumerable<string> ShippedBlocklistTerms()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(OnDiskConfigPath()));
        var root = doc.RootElement;
        foreach (var arrayName in new[] { "blockedSubstrings", "blockedExact" })
            foreach (var entry in root.GetProperty(arrayName).EnumerateArray())
                yield return entry.GetString()!;
    }
}
