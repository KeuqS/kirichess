using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// The two normalizations Phase 8a.4 runs on (see Phase 8a.4 scope decision 1).
/// They are deliberately different and must not be merged:
///
/// <list type="bullet">
/// <item><b>Identity</b> is the uniqueness key. It backs the unique index on
/// <c>ApplicationUser.NormalizedDisplayName</c>, and it is what makes "Bishop0", "BishopO",
/// "B i s h o p O" and "Вishop0" (Cyrillic В) the same name.</item>
/// <item><b>Matching</b> is the profanity key: the identity key plus repeat-collapsing, so
/// "fuuuuuck" matches "fuck".</item>
/// </list>
///
/// Repeat-collapsing must never reach the identity key: it would make "Anna" and "Ana" the same
/// account name, which is a false collision against two real people's real names.
///
/// Both functions are <b>total</b> - they accept any string, including the pre-8a.4 display names that
/// were written before any charset rule existed. The ASCII-only charset rule lives in
/// <see cref="ContentPolicy"/>, which gates new names; this class must still be able to key a
/// grandfathered one.
/// </summary>
public static class DisplayNameNormalizer
{
    /// <summary>
    /// Lookalike folding. Two groups, both chosen for what they actually defend against:
    ///
    /// <para><b>Cyrillic/Greek homoglyphs</b> - these can no longer arrive through the signup form
    /// (ContentPolicy refuses non-ASCII outright), but they are still needed here, because names
    /// created before 8a.4 may contain anything and the moderator scan has to key them.</para>
    ///
    /// <para><b>Leetspeak</b> - this is the live threat under an ASCII-only charset, and the reason
    /// this table cannot be deleted. "B0b" and "Bob" are both perfectly legal ASCII.</para>
    ///
    /// Digits 2, 6 and 9 are absent on purpose: no Latin letter looks enough like them to be worth a
    /// false collision. Every value below is distinct per source character, so two different digit
    /// strings can never fold to the same result.
    /// </summary>
    private static readonly FrozenDictionary<char, char> Confusables = new Dictionary<char, char>
    {
        // Cyrillic
        ['а'] = 'a', ['А'] = 'a',   // а А
        ['в'] = 'b', ['В'] = 'b',   // в В
        ['с'] = 'c', ['С'] = 'c',   // с С
        ['е'] = 'e', ['Е'] = 'e',   // е Е
        ['н'] = 'h', ['Н'] = 'h',   // н Н
        ['і'] = 'i', ['І'] = 'i',   // і І
        ['к'] = 'k', ['К'] = 'k',   // к К
        ['м'] = 'm', ['М'] = 'm',   // м М
        ['о'] = 'o', ['О'] = 'o',   // о О
        ['р'] = 'p', ['Р'] = 'p',   // р Р
        ['ѕ'] = 's', ['Ѕ'] = 's',   // ѕ Ѕ
        ['т'] = 't', ['Т'] = 't',   // т Т
        ['х'] = 'x', ['Х'] = 'x',   // х Х
        ['у'] = 'y', ['У'] = 'y',   // у У

        // Greek
        ['α'] = 'a', ['Α'] = 'a',
        ['Β'] = 'b',
        ['ε'] = 'e', ['Ε'] = 'e',
        ['Η'] = 'h',
        ['Κ'] = 'k',
        ['Μ'] = 'm',
        ['ο'] = 'o', ['Ο'] = 'o',
        ['ρ'] = 'p', ['Ρ'] = 'p',
        ['τ'] = 't', ['Τ'] = 't',
        ['Χ'] = 'x',

        // Leetspeak and symbol substitution
        ['0'] = 'o', ['1'] = 'l', ['3'] = 'e', ['4'] = 'a',
        ['5'] = 's', ['7'] = 't', ['8'] = 'b',
        ['@'] = 'a', ['$'] = 's', ['!'] = 'i', ['|'] = 'l', ['+'] = 't',
    }.ToFrozenDictionary();

    /// <summary>
    /// The uniqueness key. Empty when the input has no alphanumeric substance at all (a name made
    /// entirely of punctuation or emoji) - callers must treat "" as "cannot be keyed", never as a
    /// valid key, because a unique index would let exactly one such row exist.
    /// </summary>
    public static string NormalizeForIdentity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // FormKD folds compatibility forms - fullwidth "ｆｕｃｋ" becomes "fuck", the letterlike "ⅿ"
        // becomes "m" - and splits accented letters into base + combining mark, which the
        // NonSpacingMark filter below then drops so "Ｂöb" and "Bob" agree.
        var decomposed = raw.Trim().Normalize(NormalizationForm.FormKD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            var folded = Confusables.TryGetValue(ch, out var mapped) ? mapped : char.ToLowerInvariant(ch);

            // Everything else is dropped, not rejected: separators, punctuation, zero-width joiners,
            // bidi overrides and both halves of a surrogate pair all fall out here. That is what makes
            // "B o b", "B_o_b" and "B‮ob" all key to "bob".
            if (folded is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(folded);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The profanity-matching key: the identity key with runs of a repeated character collapsed to a
    /// single one. Used only by <see cref="ContentPolicy"/>; never persisted, never compared for
    /// uniqueness.
    /// </summary>
    public static string NormalizeForMatching(string? raw) => CollapseRepeats(NormalizeForIdentity(raw));

    private static string CollapseRepeats(string identity)
    {
        if (identity.Length == 0) return string.Empty;

        var sb = new StringBuilder(identity.Length);
        foreach (var ch in identity)
            if (sb.Length == 0 || sb[^1] != ch) sb.Append(ch);

        return sb.ToString();
    }

    /// <summary>
    /// 1 or 2 identity keys: the canonical one (<see cref="NormalizeForIdentity"/>, unchanged), plus
    /// the '1'-as-'i' reading when it differs from the canonical '1'-as-'l' reading.
    ///
    /// '1' is genuinely ambiguous - it reads as both 'l' and 'i'. <see cref="NormalizeForIdentity"/>
    /// resolves it to 'l' and that resolution does not change here or anywhere else: 'l' is the better
    /// impersonation answer ("A1ice" must collide with "Alice" in the unique index), so it is what
    /// gets persisted. But policy checks (ContentPolicy's reserved/blocked rules) need to see the 'i'
    /// reading too, because profanity evasion overwhelmingly uses '1' as 'i' ("sh1t", "b1tch", "4dm1n"
    /// for "admin") and under the 'l' reading alone every one of those slips past a term list keyed on
    /// real words.
    ///
    /// <b>These variants are a read-time policy concern only.</b> Nothing about them ever reaches the
    /// database: the persisted <c>NormalizedDisplayName</c> is always exactly
    /// <see cref="NormalizeForIdentity"/>'s output - variant zero, never a variant - and the unique
    /// index and <c>KiriChessDbContext.SaveChanges</c> override are untouched by this method's
    /// existence.
    /// </summary>
    public static IReadOnlyList<string> IdentityVariants(string? raw)
    {
        var primary = NormalizeForIdentity(raw);
        var alt = NormalizeForIdentity(raw?.Replace('1', 'i'));

        if (alt.Length == 0 || string.Equals(alt, primary, StringComparison.Ordinal))
            return primary.Length == 0 ? [] : [primary];

        return primary.Length == 0 ? [alt] : [primary, alt];
    }

    /// <summary>
    /// The <see cref="IdentityVariants"/> with repeat-runs collapsed - what ContentPolicy's blocklist
    /// (blockedExact/blockedSubstrings) and reservedSubstrings rules are tested against. Never
    /// persisted, never compared for uniqueness - see the note on <see cref="IdentityVariants"/>.
    /// </summary>
    public static IReadOnlyList<string> MatchingVariants(string? raw)
    {
        var identities = IdentityVariants(raw);
        if (identities.Count == 0) return [];
        if (identities.Count == 1) return [CollapseRepeats(identities[0])];

        var first = CollapseRepeats(identities[0]);
        var second = CollapseRepeats(identities[1]);
        return string.Equals(first, second, StringComparison.Ordinal) ? [first] : [first, second];
    }

    /// <summary>
    /// Every key a term list is tested for containment against: the <see cref="IdentityVariants"/>
    /// plus the <see cref="MatchingVariants"/>, de-duplicated.
    ///
    /// Both forms are needed because the two evasions pull in opposite directions. A padded candidate
    /// ("fuuuck") is only caught by the collapsed form. A term that itself contains an intentional
    /// repeat ("kkk") is only ever caught by the UNcollapsed form - by construction a collapsed key
    /// has no adjacent duplicate characters, so it can never contain such a term.
    ///
    /// This is also why term lists are normalized with <see cref="NormalizeForIdentity"/> and never
    /// with <see cref="NormalizeForMatching"/>: collapsing "kkk" down to "k" turns a slur entry into a
    /// single letter and refuses every name containing a k. See ContentPolicy's constructor.
    /// </summary>
    public static IReadOnlyList<string> PolicyKeys(string? raw)
    {
        var keys = new List<string>(4);

        foreach (var key in IdentityVariants(raw))
        {
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);

            var collapsed = CollapseRepeats(key);
            if (collapsed.Length > 0 && !keys.Contains(collapsed, StringComparer.Ordinal)) keys.Add(collapsed);
        }

        return keys;
    }
}
