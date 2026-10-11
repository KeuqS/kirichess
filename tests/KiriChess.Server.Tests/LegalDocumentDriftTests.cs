using System.Text.RegularExpressions;
using KiriChess.Server.Auth;
using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Guards the five-place duplication 8a.2 documented the hard way: <see cref="TermsVersion.Current"/>,
/// the <c>Last updated</c> / <c>Version</c> line in docs/legal/TERMS.md and docs/legal/PRIVACY.md, and
/// the two hand-maintained copies of those documents at ui/app/terms/page.tsx and
/// ui/app/privacy/page.tsx. The .tsx pages are duplicated prose, not renders of the Markdown, so
/// nothing about editing one makes the others move.
///
/// This has already failed once in production shape rather than in theory: an edit bumped PRIVACY.md
/// alone, which left the deployed page showing the previous date <i>and missing a whole section</i>
/// while every new registration was stamped with a version string matching no document a user could
/// read. Both halves of that are asserted here, because the version date alone would not have caught
/// the missing section and the missing section alone would not have caught the version date.
///
/// The right long-term fix is to render the Markdown rather than duplicate it, which is a UI change
/// this test deliberately does not pre-empt; until then the drift is silent and this is what makes
/// it loud. Same file-walking approach as <see cref="HubContractTests"/>, including its lower-bound
/// guard: a regex that quietly stops matching is a test that passes for the wrong reason.
/// </summary>
public sealed class LegalDocumentDriftTests
{
    private const string TermsMarkdown = "docs/legal/TERMS.md";
    private const string PrivacyMarkdown = "docs/legal/PRIVACY.md";
    private const string TermsPage = "ui/app/[locale]/terms/page.tsx";
    private const string PrivacyPage = "ui/app/[locale]/privacy/page.tsx";

    /// <summary>
    /// Matches both renderings of the same line: <c>**Last updated: 2026-08-16** - **Version: …**</c>
    /// in Markdown and <c>Last updated: 2026-08-16 - Version: …</c> in JSX. Requiring a date right
    /// after the colon is what excludes the prose references to a `Last updated` / `Version` date in
    /// each document's own "Changes to this policy" section.
    /// </summary>
    private static readonly Regex VersionDateRegex =
        new(@"(?:Last updated|Version):\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    /// <summary>Section numbers only: the prose after them legitimately differs between the Markdown
    /// and the JSX (typographic quotes, a shortened parenthetical), and asserting on it would produce
    /// a guard so noisy it gets deleted. The numbering is the part that must not drift.</summary>
    private static readonly Regex MarkdownSectionRegex = new(@"^## (\d+)\.", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex JsxSectionRegex = new(@"<h2[^>]*>\s*(\d+)\.", RegexOptions.Compiled);

    /// <summary>Every level-2 Markdown heading, numbered or not (so <c>## 2b.</c> still ends section 2's
    /// chunk instead of being swallowed into it). Chunk boundaries only; numbering is decided per-chunk
    /// against <see cref="MarkdownSectionRegex"/> below.</summary>
    private static readonly Regex MarkdownHeadingLineRegex = new(@"^## .*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Every JSX <c>&lt;h2&gt;</c> opening tag, numbered or not, for the same reason.</summary>
    private static readonly Regex JsxHeadingTagRegex = new(@"<h2[^>]*>", RegexOptions.Compiled);

    /// <summary>Top-level Markdown bullets. This repo's legal docs never nest a list, so column-0
    /// <c>- </c> is unambiguous; a nested list would need indentation-aware matching this does not do.</summary>
    private static readonly Regex MarkdownListItemRegex = new(@"^- ", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A Markdown table row, header and data rows alike, excluding the <c>| --- | --- |</c>
    /// separator line itself (matched separately by <see cref="MarkdownTableSeparatorRegex"/>).</summary>
    private static readonly Regex MarkdownTableRowRegex = new(@"^\|(?![\s|:-]*-{2,}[\s|:-]*\|?\s*$).+\|\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The <c>| --- | --- |</c> line under a table's header row. Exactly one per table, so
    /// this doubles as "how many tables are in this chunk", which is what lets
    /// <see cref="EntryCount"/> subtract exactly one header row per table rather than guessing.</summary>
    private static readonly Regex MarkdownTableSeparatorRegex = new(@"^\|[\s|:-]*-{2,}[\s|:-]*\|?\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A JSX list item, however it is opened (plain <c>&lt;li&gt;</c> or with a className).</summary>
    private static readonly Regex JsxListItemRegex = new(@"<li[\s>]", RegexOptions.Compiled);

    /// <summary>Anything that renders one table row: a literal <c>&lt;tr&gt;</c> (the header row, written
    /// inline) or a capitalised <c>...Row</c> component call such as <c>&lt;SubProcessorRow</c> or
    /// <c>&lt;RetentionRow</c>. Both PRIVACY.md tables in this file factor their repeated row markup into
    /// a small component, so counting literal <c>&lt;tr&gt;</c> alone only ever finds the header, never a
    /// data row - this is the JSX-side reason <see cref="EntryCount"/> cannot just count tags 1:1.</summary>
    private static readonly Regex JsxRowLikeTagRegex = new(@"<tr[\s>]|<[A-Z]\w*Row\b", RegexOptions.Compiled);

    /// <summary>One per JSX table, the same role <see cref="MarkdownTableSeparatorRegex"/> plays on the
    /// Markdown side: how many header rows to subtract from <see cref="JsxRowLikeTagRegex"/>'s count.</summary>
    private static readonly Regex JsxTheadRegex = new(@"<thead[\s>]", RegexOptions.Compiled);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(OpenApiExport.FindRepoRoot(), relativePath));

    /// <summary>
    /// Splits <paramref name="content"/> into chunks at every heading matched by
    /// <paramref name="headingBoundaryRegex"/>, keeps only the chunks whose heading also matches
    /// <paramref name="numberedHeadingRegex"/> (so an un-numbered heading such as Markdown's
    /// <c>## 2b.</c> or its JSX mirror still ends the previous chunk, but contributes no entry of its
    /// own), and returns one combined "how many things does this section disclose" count per chunk:
    /// list items plus table data rows (header rows excluded on both sides via
    /// <paramref name="headerRowRegex"/>, one subtracted per table found by <paramref name="tableRegex"/>).
    ///
    /// List items and table rows are combined into a single number, not compared separately, because
    /// PRIVACY.md's own §2 is a Markdown table rendered as a plain JSX list on the page (a pre-existing,
    /// deliberate choice, not drift): comparing "list items" against "list items" and "table rows"
    /// against "table rows" independently would report that pairing as a permanent, spurious mismatch.
    /// The combined count still catches exactly what L-1-1 was about, an item added to one side and not
    /// the other, whichever shape either side happens to use for it.
    /// </summary>
    private static Dictionary<string, int> EntryCount(
        string content, Regex headingBoundaryRegex, Regex numberedHeadingRegex,
        Regex listItemRegex, Regex rowRegex, Regex headerRowRegex)
    {
        var headings = headingBoundaryRegex.Matches(content).Cast<Match>().ToList();
        var result = new Dictionary<string, int>();

        for (var i = 0; i < headings.Count; i++)
        {
            var numberMatch = numberedHeadingRegex.Match(content, headings[i].Index);
            if (!numberMatch.Success || numberMatch.Index != headings[i].Index)
            {
                continue; // an un-numbered heading (e.g. "2b."): a chunk boundary, not a section of its own
            }

            var chunkStart = headings[i].Index + headings[i].Length;
            var chunkEnd = i + 1 < headings.Count ? headings[i + 1].Index : content.Length;
            var chunk = content[chunkStart..chunkEnd];

            var listItems = listItemRegex.Matches(chunk).Count;
            var rows = rowRegex.Matches(chunk).Count; // header row(s) + data row(s), one table row-count each
            var headerRows = headerRowRegex.Matches(chunk).Count; // one per table: what to subtract from `rows`
            result[numberMatch.Groups[1].Value] = listItems + (rows - headerRows);
        }

        return result;
    }

    [Theory]
    [InlineData(TermsMarkdown)]
    [InlineData(PrivacyMarkdown)]
    [InlineData(TermsPage)]
    [InlineData(PrivacyPage)]
    public void Every_published_copy_carries_the_version_stamped_at_registration(string relativePath)
    {
        var dates = VersionDateRegex.Matches(Read(relativePath))
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Two per file: "Last updated" and "Version". Zero would mean the line was reformatted out of
        // the regex's reach, which must fail rather than vacuously pass.
        Assert.True(dates.Count >= 2,
            $"{relativePath}: found {dates.Count} version dates, expected at least 2 (Last updated, Version). "
            + "The header line was reformatted; fix this regex, do not delete the assertion.");

        Assert.All(dates, date => Assert.Equal(TermsVersion.Current, date));
    }

    /// <summary>
    /// The other half of the 8a.2 incident: a section added to the Markdown and never mirrored onto
    /// the page. Section numbers are compared as a sequence, so an added, removed or misnumbered
    /// section fails whichever side it happens on.
    /// </summary>
    [Theory]
    [InlineData(TermsMarkdown, TermsPage)]
    [InlineData(PrivacyMarkdown, PrivacyPage)]
    public void Every_section_of_the_document_exists_on_the_page(string markdownPath, string pagePath)
    {
        var markdownSections = MarkdownSectionRegex.Matches(Read(markdownPath))
            .Select(m => m.Groups[1].Value).ToList();
        var pageSections = JsxSectionRegex.Matches(Read(pagePath))
            .Select(m => m.Groups[1].Value).ToList();

        Assert.True(markdownSections.Count > 0, $"{markdownPath}: no numbered sections matched; the heading format changed.");
        Assert.True(pageSections.Count > 0, $"{pagePath}: no numbered <h2> sections matched; the page structure changed.");

        Assert.Equal(
            string.Join(", ", markdownSections),
            string.Join(", ", pageSections));
    }

    /// <summary>
    /// The third shape of drift L-1-1 found: content added <i>inside</i> an existing numbered section,
    /// which <see cref="Every_section_of_the_document_exists_on_the_page"/> cannot see because that test
    /// only compares the sequence of section numbers, not what is inside them. §2 gaining three new
    /// data-group rows, or §5 gaining six new retention rows, leaves the section-number sequence
    /// unchanged on both sides while the page quietly stops disclosing what the Markdown discloses.
    ///
    /// This does not compare prose (typographic quotes, a reworded parenthetical legitimately differ
    /// between the two, see the section-number test's own doc comment); it compares the one thing that
    /// is structural and therefore must match: how many discrete things a section discloses, list items
    /// and table rows combined (see <see cref="EntryCount"/> for why the two are combined rather than
    /// compared side by side). A per-section count that goes 15/9 (Markdown/page) is exactly what the
    /// L-1-1 packet looked like before the fix; this guard is what would have caught it automatically.
    /// </summary>
    [Theory]
    [InlineData(TermsMarkdown, TermsPage)]
    [InlineData(PrivacyMarkdown, PrivacyPage)]
    public void Every_sections_disclosed_item_count_matches_the_page(string markdownPath, string pagePath)
    {
        var markdownCounts = EntryCount(
            Read(markdownPath), MarkdownHeadingLineRegex, MarkdownSectionRegex,
            MarkdownListItemRegex, MarkdownTableRowRegex, MarkdownTableSeparatorRegex);
        var pageCounts = EntryCount(
            Read(pagePath), JsxHeadingTagRegex, JsxSectionRegex,
            JsxListItemRegex, JsxRowLikeTagRegex, JsxTheadRegex);

        // Lower-bound guard, same reasoning as the other two tests in this file: if either side's
        // regex stopped matching, every count would be zero and the loop below would vacuously pass.
        Assert.True(markdownCounts.Count > 0, $"{markdownPath}: no numbered sections matched; the heading format changed.");
        Assert.True(pageCounts.Count > 0, $"{pagePath}: no numbered <h2> sections matched; the page structure changed.");
        Assert.True(markdownCounts.Values.Sum() > 0,
            $"{markdownPath}: every section counted zero disclosed items; the list/table regex stopped matching.");
        Assert.True(pageCounts.Values.Sum() > 0,
            $"{pagePath}: every section counted zero disclosed items; the list/table regex stopped matching.");

        var mismatches = markdownCounts.Keys
            .Union(pageCounts.Keys)
            .OrderBy(number => int.Parse(number))
            .Select(number =>
            {
                markdownCounts.TryGetValue(number, out var md);
                pageCounts.TryGetValue(number, out var page);
                return (number, md, page);
            })
            .Where(x => x.md != x.page)
            .Select(x => $"§{x.number}: Markdown discloses {x.md} item(s), page discloses {x.page}")
            .ToList();

        Assert.True(mismatches.Count == 0,
            $"{pagePath} is missing content its own Markdown source has, or has content the Markdown does not:\n"
            + string.Join("\n", mismatches));
    }

    [Fact]
    public void Privacy_section_8_keeps_self_delete_and_the_mailbox_path()
    {
        // B11a / L-2-3, owner 2026-09-08. The overclaim this replaces was that a ban makes the
        // self-serve erasure button unreachable. It does not, not while the access token is live.
        var markdown = Read(PrivacyMarkdown);
        var page = Read(PrivacyPage);
        const string pin = "a ban is not a second precondition on deletion";
        Assert.Contains(pin, markdown, StringComparison.Ordinal);
        Assert.Contains(pin, page, StringComparison.Ordinal);
        Assert.Contains("privacy@kirichess.com", markdown, StringComparison.Ordinal);
        Assert.Contains("privacy@kirichess.com", page, StringComparison.Ordinal);
        Assert.DoesNotContain("erasure buttons are unreachable", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("erasure buttons are unreachable", page, StringComparison.Ordinal);
    }
}
