using System.Reflection;
using System.Text.RegularExpressions;
using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.OpenApi;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// L-3-5's action item: a ROPA drift guard in the shape of <see cref="LegalDocumentDriftTests"/>, for
/// the specific failure mode that let docs/legal/ROPA.md go stale in the first place. Three processing
/// activities (§3.5 notification mail, §3.6 Sentry tracing, §3.8 web push) landed in code with no ROPA
/// row until a later bundle caught up by hand; nothing forced that catch-up to happen.
///
/// The check: every <see cref="DbSet{TEntity}"/> declared directly on <see cref="KiriChessDbContext"/>
/// must either map to a ROPA §3.x processing activity that still exists as a heading in ROPA.md, or
/// appear in <see cref="Exemptions"/> with a one-line reason. A table with neither is either a new
/// feature that shipped without its ROPA row (an oversight - the exact shape L-3-5 is about) or a table
/// this test's author hasn't classified yet; either way the fix is to add a row to
/// <see cref="RopaSectionByTable"/> or <see cref="Exemptions"/>, not to relax this test.
///
/// Scope: only DbSet properties declared on <c>KiriChessDbContext</c> itself, not the ones it inherits
/// from <c>IdentityDbContext</c> (Users, Roles, UserClaims, UserLogins, UserRoles, RoleClaims,
/// UserTokens). Those are framework-owned tables fixed by the base class, not application surface this
/// repository adds to, and ROPA §3.1 already covers the identity data they hold by name (email, password
/// hash, TOTP secret, and so on) without needing a table-name-shaped anchor for each one.
/// </summary>
public sealed class RopaDriftTests
{
    private const string RopaMarkdown = "docs/legal/ROPA.md";

    /// <summary>A <c>### 3.x</c> processing-activity heading in ROPA.md.</summary>
    private static readonly Regex RopaSectionHeadingRegex =
        new(@"^### (\d+\.\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Every <see cref="KiriChessDbContext"/>-declared table this test currently knows how to place,
    /// mapped to the ROPA §3.x processing activity that documents it. Add a line here when a new table
    /// gets its ROPA row; the section number is re-verified against ROPA.md's actual headings below, so
    /// a section that is later renumbered or deleted fails this test too, not just a missing table.
    /// </summary>
    private static readonly Dictionary<string, string> RopaSectionByTable = new()
    {
        ["Games"] = "3.2",
        ["GameMoves"] = "3.2",
        ["Ratings"] = "3.2",
        ["RatingHistories"] = "3.2",
        ["RefreshTokens"] = "3.1",
        ["ApiTokens"] = "3.1",
        ["ExternalLoginCodes"] = "3.1",
        ["TwoFactorChallenges"] = "3.1",
        ["Reports"] = "3.3",
        ["UserSanctions"] = "3.3",
        ["WebPushSubscriptions"] = "3.8",
        ["Challenges"] = "3.9",
        ["RematchOffers"] = "3.9",
        ["DonationReferences"] = "3.10",
    };

    /// <summary>
    /// Tables that genuinely need no ROPA row, each with the one-line reason a reviewer needs to tell a
    /// decision from an oversight. Do not add a table here just to make this test pass; a table that
    /// processes personal data belongs in <see cref="RopaSectionByTable"/> pointing at a real ROPA
    /// section, and if no such section exists yet, that is this test correctly reporting a gap in
    /// docs/legal/ROPA.md, not a bug in the test.
    /// </summary>
    private static readonly Dictionary<string, string> Exemptions = new()
    {
        ["KillSwitches"] = "Operational feature-flag state, one row per KiriChess.Server.Infrastructure.KillSwitch member (on/off), carries no personal data of any data subject.",
        ["DailyCounters"] = "Anonymous, unidentifiable traffic counters. PRIVACY.md §2 and §3 state in terms that this table holds no personal data of any kind and sits outside GDPR Art. 6 entirely, which puts it outside Art. 30's scope (this record) for the same reason.",
        ["DataProtectionKeys"] = "ASP.NET Core's Data Protection key ring: cryptographic key material used to protect tokens, not personal data about a data subject. See docs/decisions/RETENTION.md's own characterization of this table.",
    };

    /// <summary>
    /// Tables that <em>should</em> have a ROPA row and do not. This is not a second exemption list and
    /// must not be used as one: each entry is a recorded gap with an owner, and the list is expected to
    /// shrink to empty. It existed because the guard was written on 2026-09-01, after tables had
    /// already shipped without their rows, and a guard that starts red is a guard somebody deletes.
    /// The last gap (<c>DonationReferences</c> / L-3-2) closed on 2026-09-04. Do not add to this list.
    /// </summary>
    private static readonly Dictionary<string, string> KnownGaps = new();

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(OpenApiExport.FindRepoRoot(), relativePath));

    private static List<string> DeclaredDbSetPropertyNames() =>
        typeof(KiriChessDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.Name)
            .ToList();

    [Fact]
    public void Every_declared_DbSet_has_a_ROPA_row_or_a_named_exemption()
    {
        var ropaSections = RopaSectionHeadingRegex.Matches(Read(RopaMarkdown))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        // Lower-bound guard, same reasoning as LegalDocumentDriftTests: if the heading regex stopped
        // matching, every lookup below would find nothing and the test would fail for the wrong reason
        // (or, worse, a differently-broken regex could vacuously report every mapped section as gone).
        Assert.True(ropaSections.Count > 0,
            $"{RopaMarkdown}: no '### 3.x' processing-activity headings matched; the heading format changed.");

        var dbSetNames = DeclaredDbSetPropertyNames();
        Assert.True(dbSetNames.Count > 0,
            "Reflection found no DbSet<> properties declared on KiriChessDbContext; the guard broke, not the schema.");

        var problems = new List<string>();

        foreach (var name in dbSetNames)
        {
            if (Exemptions.ContainsKey(name) || KnownGaps.ContainsKey(name))
            {
                continue;
            }

            if (!RopaSectionByTable.TryGetValue(name, out var section))
            {
                problems.Add($"{name}: no entry in RopaSectionByTable and no entry in Exemptions. "
                    + "Either this table processes personal data and needs a ROPA.md row (add the row, "
                    + "then map it here), or it genuinely does not and belongs in Exemptions with a reason.");
                continue;
            }

            if (!ropaSections.Contains(section))
            {
                problems.Add($"{name}: mapped to ROPA §{section}, but that heading no longer exists in "
                    + $"{RopaMarkdown}. The section was renumbered or removed; update this mapping.");
            }
        }

        Assert.True(problems.Count == 0,
            "The following KiriChessDbContext tables have no verifiable ROPA coverage:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// The baseline can shrink and must never grow. <see cref="KnownGaps"/> is the set of tables that
    /// were already missing a ROPA row when the guard above was written; once a gap is closed its entry
    /// is deleted, and this fails if anyone deletes an entry without also adding the row, or quietly
    /// parks a new table there instead of documenting it.
    /// </summary>
    [Fact]
    public void The_known_gap_baseline_only_shrinks()
    {
        var ropaSections = RopaSectionHeadingRegex.Matches(Read(RopaMarkdown))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        Assert.True(ropaSections.Contains("3.10"),
            $"{RopaMarkdown} must keep §3.10 (donations) now that DonationReferences is mapped there. "
            + "If that section is gone, put DonationReferences back in KnownGaps only as a last resort.");

        Assert.True(KnownGaps.Count == 0,
            $"KnownGaps has {KnownGaps.Count} entries. The last baseline gap closed with L-3-2 "
            + "(DonationReferences / ROPA §3.10). Do not park a new table here: a table added from "
            + "here on gets its docs/legal/ROPA.md row in the same change that adds the table.");

        foreach (var (table, reason) in KnownGaps)
        {
            Assert.False(RopaSectionByTable.ContainsKey(table),
                $"{table} is in both KnownGaps and RopaSectionByTable. If its ROPA row now exists, delete "
                + "the KnownGaps entry; a table cannot be both documented and a known gap.");

            Assert.False(string.IsNullOrWhiteSpace(reason),
                $"{table}: a KnownGaps entry needs the reason that tells a reviewer what is missing and who owns it.");
        }
    }
}
