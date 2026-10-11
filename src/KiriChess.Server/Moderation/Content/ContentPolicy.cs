using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// The pure half of Phase 8a.4: length, charset, separator placement, substance, reserved names and
/// profanity - everything decidable from the candidate string alone. No <c>DbContext</c>, no DI
/// dependencies, no uniqueness, no cooldown; see <see cref="DisplayNameService"/> for the
/// database-aware half.
///
/// The embedded blocklist (<c>config/kirichess.moderation.json</c>) is parsed once per process,
/// mirroring <c>RulesDocument.Instance</c> in <c>KiriChess.Engine</c>. The list is not exhaustive and
/// is not meant to be - it is a speed bump, and the moderator scan plus the report queue are what
/// actually catch what it misses.
/// </summary>
public sealed class ContentPolicy
{
    private const string ResourceName = "KiriChess.Server.kirichess.moderation.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly char[] Separators = [' ', '_', '.', '-'];

    private readonly HashSet<string> _blockedExact;
    private readonly IReadOnlyList<string> _blockedSubstrings;
    private readonly HashSet<string> _reservedExact;
    private readonly IReadOnlyList<string> _reservedSubstrings;
    private readonly IReadOnlyList<string> _allowedSubstrings;

    private ContentPolicy(BlocklistDocument doc)
    {
        // Every list is re-normalized on load, even though config/kirichess.moderation.json already
        // ships its entries lowercase and separator-free: a hand-edited entry with a stray capital
        // must not silently stop matching.
        //
        // ALL of them normalize with NormalizeForIdentity - never NormalizeForMatching. Running a term
        // list through the repeat-collapsing transform is a live footgun, and it shipped once already:
        // the entry "kkk" collapses to the single character "k", after which EVERY display name
        // containing a k ("Knight", "Kasparov", "Mike") is refused as profanity. A term list must keep
        // its intentional repeats.
        //
        // Matching still catches "fuuuck", because the CANDIDATE is tested in both forms - see
        // PolicyKeys below. A term that itself contains a repeat ("kkk") simply never matches a
        // collapsed key, which is correct rather than merely safe: by construction a collapsed key has
        // no adjacent duplicate characters, so it cannot contain such a term at all.
        _blockedExact = doc.BlockedExact
            .Select(DisplayNameNormalizer.NormalizeForIdentity)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        _blockedSubstrings = doc.BlockedSubstrings
            .Select(DisplayNameNormalizer.NormalizeForIdentity)
            .Where(s => s.Length > 0)
            .ToArray();

        _reservedExact = doc.ReservedExact
            .Select(DisplayNameNormalizer.NormalizeForIdentity)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        _reservedSubstrings = doc.ReservedSubstrings
            .Select(DisplayNameNormalizer.NormalizeForIdentity)
            .Where(s => s.Length > 0)
            .ToArray();

        _allowedSubstrings = doc.AllowedSubstrings
            .Select(DisplayNameNormalizer.NormalizeForIdentity)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    /// <summary>The embedded blocklist, parsed once per process. Mirrors RulesDocument.Instance.</summary>
    public static ContentPolicy Default { get; } = LoadEmbedded();

    private static ContentPolicy LoadEmbedded()
    {
        using var stream = typeof(ContentPolicy).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded moderation config '{ResourceName}' is missing. The build must embed config/kirichess.moderation.json.");
        return Parse(stream);
    }

    private static ContentPolicy Parse(Stream stream)
    {
        var doc = JsonSerializer.Deserialize<BlocklistDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Moderation config deserialized to null.");
        return new ContentPolicy(doc);
    }

    /// <summary>
    /// Builds a policy from an inline JSON document instead of the shipped blocklist, so tests can
    /// assert against a small, deliberately chosen list (e.g. the Scunthorpe boundary-evasion case)
    /// rather than the full production list.
    /// </summary>
    internal static ContentPolicy Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<BlocklistDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Moderation config deserialized to null.");
        return new ContentPolicy(doc);
    }

    /// <summary>
    /// Everything that can be decided from the string alone: length, charset, separator placement,
    /// substance, reserved names, profanity. Uniqueness and cooldown are NOT decided here - they need
    /// the database, and that is DisplayNameService's job.
    ///
    /// Options are passed in rather than injected so this class stays pure and its tests need no host.
    ///
    /// Rules are evaluated in order and this returns on the first failure. The order is cheapest-first
    /// and, more importantly, means a 40-character string of profanity reports TooLong rather than
    /// Profanity - the length rule is the one the player can actually act on.
    /// </summary>
    public DisplayNameVerdict CheckDisplayName(string? candidate, ContentModerationOptions options)
    {
        var normalized = DisplayNameNormalizer.NormalizeForIdentity(candidate);
        var trimmed = candidate?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(candidate) || trimmed.Length < options.MinLength)
            return new DisplayNameVerdict(false, DisplayNameRejection.TooShort,
                $"Display names need at least {options.MinLength} characters.", normalized, options.MinLength);

        if (trimmed.Length > options.MaxLength)
            return new DisplayNameVerdict(false, DisplayNameRejection.TooLong,
                $"Display names can be at most {options.MaxLength} characters.", normalized, options.MaxLength);

        // Rule 3 is ASCII-only, deliberately (README scope decision 9). The alternative - accepting
        // arbitrary Unicode - means either accepting every Cyrillic/Greek homoglyph impersonation
        // there is, or shipping and maintaining a real Unicode confusables table and keeping it
        // current. Neither is defensible at this size, and this is the same rule Lichess and
        // Chess.com both landed on. The cost is real and is not dressed up: a player whose real name
        // is not Latin-script must pick a Latin handle.
        if (!IsAsciiCharset(trimmed))
            return new DisplayNameVerdict(false, DisplayNameRejection.IllegalCharacter,
                "Display names can use letters A-Z, numbers, spaces, and - _ . only.", normalized);

        if (HasBadSeparatorPlacement(trimmed))
            return new DisplayNameVerdict(false, DisplayNameRejection.SeparatorPlacement,
                "Display names can't start or end with a space, dot, dash or underscore, or use two in a row.", normalized);

        if (normalized.Length < 2)
            return new DisplayNameVerdict(false, DisplayNameRejection.NotEnoughSubstance,
                "Display names need at least two letters or numbers.", normalized);

        // Reserved: identity-variant equality against reservedExact, matching-variant containment
        // against reservedSubstrings (README rule 6, extended for the '1' ambiguity - see
        // DisplayNameNormalizer.IdentityVariants). "4dm1n" has to reach "admin" here via the 'i'
        // reading; the persisted NormalizedDisplayName is unaffected either way, since that is always
        // NormalizeForIdentity's single canonical output, never a variant.
        // Exact rules test the IDENTITY variants only. Including the collapsed forms here would
        // over-match short entries - "Mee" collapses to "me", which is a reservedExact entry, and
        // refusing "Mee" is not what that entry is for. Containment rules test both forms (PolicyKeys).
        var identityVariants = DisplayNameNormalizer.IdentityVariants(candidate);
        var policyKeys = DisplayNameNormalizer.PolicyKeys(candidate);

        if (identityVariants.Any(v => _reservedExact.Contains(v)) ||
            policyKeys.Any(v => _reservedSubstrings.Any(term => v.Contains(term, StringComparison.Ordinal))))
        {
            return new DisplayNameVerdict(false, DisplayNameRejection.Reserved,
                "That name is reserved. Try another.", normalized);
        }

        // Blocked: matching-variant equality against blockedExact, allowlist-aware matching-variant
        // containment against blockedSubstrings (README rule 7, same '1' extension). No blocklist term
        // ever appears in the message.
        if (MatchesBlocklist(identityVariants, policyKeys))
        {
            return new DisplayNameVerdict(false, DisplayNameRejection.Profanity,
                "That name isn't allowed. Try another.", normalized);
        }

        return new DisplayNameVerdict(true, null, string.Empty, normalized);
    }

    /// <summary>
    /// Surface-agnostic (README ground-truth correction 1 / scope decision 1): true when the text
    /// matches the blocklist. This is the one call an eventual chat feature makes when it exists - no
    /// chat exists today, and nothing in this phase calls this method.
    /// </summary>
    public bool ContainsBlockedTerm(string? text) =>
        MatchesBlocklist(
            DisplayNameNormalizer.IdentityVariants(text),
            DisplayNameNormalizer.PolicyKeys(text));

    /// <summary>
    /// The blocklist test shared by <see cref="CheckDisplayName"/> and <see cref="ContainsBlockedTerm"/>
    /// so the two never drift: equality against <c>blockedExact</c> over the identity variants, and
    /// allowlist-aware containment against <c>blockedSubstrings</c> over every policy key (identity
    /// and repeat-collapsed alike - see DisplayNameNormalizer.PolicyKeys for why both are needed).
    /// </summary>
    private bool MatchesBlocklist(IReadOnlyList<string> identityVariants, IReadOnlyList<string> policyKeys)
    {
        if (identityVariants.Any(v => _blockedExact.Contains(v))) return true;

        foreach (var key in policyKeys)
        {
            if (key.Length == 0) continue;
            if (_blockedSubstrings.Any(term => OccursOutsideAllowlist(key, term))) return true;
        }
        return false;
    }

    private static bool IsAsciiCharset(string value)
    {
        foreach (var ch in value)
        {
            var legal = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or ' ' or '_' or '.' or '-';
            if (!legal) return false;
        }
        return true;
    }

    private static bool HasBadSeparatorPlacement(string value)
    {
        if (value.Length == 0) return false;
        if (IsSeparator(value[0]) || IsSeparator(value[^1])) return true;

        for (var i = 0; i < value.Length - 1; i++)
            if (IsSeparator(value[i]) && IsSeparator(value[i + 1])) return true;

        return false;
    }

    private static bool IsSeparator(char ch) => Array.IndexOf(Separators, ch) >= 0;

    /// <summary>
    /// True when <paramref name="term"/> occurs in <paramref name="matchKey"/> at a position that is
    /// NOT fully inside an allowlisted word. This is the Scunthorpe carve-out, and it is done
    /// positionally rather than by stripping allowlisted words out of the key first: stripping
    /// "class" from "classhit" leaves "hit" and loses the "shit" that spans the boundary. Checking
    /// containment of the occurrence keeps both the false positive and the evasion closed.
    /// </summary>
    private bool OccursOutsideAllowlist(string matchKey, string term)
    {
        for (var from = 0; from + term.Length <= matchKey.Length;)
        {
            var at = matchKey.IndexOf(term, from, StringComparison.Ordinal);
            if (at < 0) return false;
            if (!IsCoveredByAllowedWord(matchKey, at, term.Length)) return true;
            from = at + 1;
        }
        return false;
    }

    private bool IsCoveredByAllowedWord(string matchKey, int at, int length)
    {
        foreach (var allowed in _allowedSubstrings)
        {
            for (var from = 0; from + allowed.Length <= matchKey.Length;)
            {
                var start = matchKey.IndexOf(allowed, from, StringComparison.Ordinal);
                if (start < 0) break;
                if (start <= at && start + allowed.Length >= at + length) return true;
                from = start + 1;
            }
        }
        return false;
    }

    private sealed class BlocklistDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("blockedSubstrings")] public IReadOnlyList<string> BlockedSubstrings { get; init; } = [];
        [JsonPropertyName("blockedExact")] public IReadOnlyList<string> BlockedExact { get; init; } = [];
        [JsonPropertyName("reservedExact")] public IReadOnlyList<string> ReservedExact { get; init; } = [];
        [JsonPropertyName("reservedSubstrings")] public IReadOnlyList<string> ReservedSubstrings { get; init; } = [];
        [JsonPropertyName("allowedSubstrings")] public IReadOnlyList<string> AllowedSubstrings { get; init; } = [];
    }
}
