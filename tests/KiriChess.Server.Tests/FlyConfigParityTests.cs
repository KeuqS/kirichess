using System.Text.RegularExpressions;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Tests;

/// <summary>
/// C-9-6: staging and production Fly configs are hand-maintained in parallel with nothing checking
/// that they only diverge on the intended keys. The visible symptom the audit named was cosmetic
/// (<c>fly.backend.toml</c>'s <c>[metrics]</c> versus <c>fly.backend.staging.toml</c>'s
/// <c>[[metrics]]</c>, both accepted by Fly) but the real risk is a prod-only tuning change (a new
/// <c>[env]</c> var, a concurrency bump, a health-check tweak) silently skipping staging, the
/// environment meant to take the first hit.
///
/// This parses the four <c>fly.*.toml</c> files with a small hand-rolled reader (good enough for
/// their shape: <c>[section]</c>/<c>[[section]]</c> headers and scalar <c>key = value</c> pairs,
/// no arrays-as-values, no inline tables) and asserts the backend pair and the UI pair each differ
/// only on an explicit allowlist of keys, every entry carrying a one-line reason (the
/// <c>CiWorkflowDriftTests</c> idiom).
///
/// <c>fly.backend.toml</c> carries a load-bearing comment explaining why ASP.NET
/// <c>AllowedHosts</c> stays unset (I-7-7): the allow-list lives on
/// <c>OriginLock__AllowedHosts</c> instead, so the 6PN metrics scrape is exempt. This test does
/// not touch either backend file's content, only reads it.
/// </summary>
public class FlyConfigParityTests
{
    private static readonly string RepoRoot = OpenApiExport.FindRepoRoot();

    private static string BackendProd => Path.Combine(RepoRoot, "fly.backend.toml");
    private static string BackendStaging => Path.Combine(RepoRoot, "fly.backend.staging.toml");
    private static string UiProd => Path.Combine(RepoRoot, "fly.ui.toml");
    private static string UiStaging => Path.Combine(RepoRoot, "fly.ui.staging.toml");

    private static readonly Regex ArrayTableHeader = new(@"^\[\[([^\]]+)\]\]$");
    private static readonly Regex TableHeader = new(@"^\[([^\]]+)\]$");
    private static readonly Regex KeyValue = new(@"^([A-Za-z0-9_.]+)\s*=\s*(.+)$");

    /// <summary>
    /// Reads a fly.toml into a flat "dotted.path" -&gt; value map. <c>[[section]]</c> is folded onto
    /// the same key prefix as <c>[section]</c> (both files here declare at most one instance of any
    /// array-of-tables, so there is no index to lose) - this is the normalisation C-9-6 asks for:
    /// <c>fly.backend.toml</c>'s <c>[metrics]</c> and <c>fly.backend.staging.toml</c>'s
    /// <c>[[metrics]]</c> land on the identical keys <c>metrics.port</c> / <c>metrics.path</c> and
    /// compare on value alone, not on which bracket form declared them.
    /// </summary>
    private static Dictionary<string, string> ParseToml(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = Array.Empty<string>();

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var arrayTable = ArrayTableHeader.Match(line);
            if (arrayTable.Success)
            {
                section = arrayTable.Groups[1].Value.Split('.');
                continue;
            }

            var table = TableHeader.Match(line);
            if (table.Success)
            {
                section = table.Groups[1].Value.Split('.');
                continue;
            }

            var kv = KeyValue.Match(line);
            if (!kv.Success)
            {
                throw new InvalidOperationException(
                    $"{path}: could not parse line as [section], [[section]], or key = value: '{rawLine}'. " +
                    "This test's TOML reader only handles the shape the four fly.*.toml files currently use " +
                    "(scalar values, no arrays or inline tables); widen ParseToml if a new form was added.");
            }

            var key = kv.Groups[1].Value.Trim();
            var value = kv.Groups[2].Value.Trim().Trim('"');
            var fullKey = section.Length == 0 ? key : string.Join(".", section) + "." + key;
            result[fullKey] = value;
        }

        return result;
    }

    /// <summary>
    /// Every key that is allowed to differ (or to exist in only one file) between a prod/staging
    /// pair, with the reason it's intended, not accidental.
    /// </summary>
    private static readonly (string Key, string Reason)[] BackendAllowlist =
    [
        ("app", "the production and staging Fly apps are deliberately separate apps (kirichess-api / kirichess-api-staging)"),
        ("env.ASPNETCORE_ENVIRONMENT", "staging deliberately runs the ASP.NET Staging environment, production runs Production"),
        ("env.OriginLock__BlockedHosts__0",
            "production-only by design: fly.backend.toml's own comment (I-2-2) explains staging must not set this, since kirichess-api-staging.fly.dev is that app's only public hostname and blocking it would 404 every request"),
        ("env.OriginLock__AllowedHosts__0",
            "each app allows only the hostname players actually use: api.kirichess.com in production, kirichess-api-staging.fly.dev on staging (I-7-7). Probe paths stay exempt so the 6PN metrics scrape is not refused"),
        ("http_service.auto_stop_machines",
            "fly.backend.staging.toml's own comment: staging carries no real players so scaling to zero between sessions is the cheaper, still-correct choice; production stays warm because a restart mid-game strands live in-memory state"),
        ("http_service.auto_start_machines", "companion of http_service.auto_stop_machines above"),
        ("http_service.min_machines_running", "companion of http_service.auto_stop_machines above"),
    ];

    private static readonly (string Key, string Reason)[] UiAllowlist =
    [
        ("app", "the production and staging Fly apps are deliberately separate apps (kirichess-ui / kirichess-ui-staging)"),
        ("build.args.NEXT_PUBLIC_API_BASE", "each UI build is baked to point at its own backend (fly.ui.staging.toml's own comment)"),
        ("build.args.NEXT_PUBLIC_SITE_URL", "canonical origin for robots.txt/sitemap/openGraph URLs; staging's is its own fly.dev hostname, not the production domain"),
        ("build.args.NEXT_PUBLIC_SENTRY_DSN",
            "fly.ui.staging.toml's own comment: staging errors would be noise in the same Sentry project production reports to, so this is intentionally left empty there"),
        ("build.args.NEXT_PUBLIC_GOOGLE_SIGN_IN",
            "fly.ui.staging.toml's own comment: staging API has no Google OAuth secrets, so the button stays off there while production bakes true"),
    ];

    private static void AssertOnlyAllowlistedKeysDiffer(
        string prodPath,
        string stagingPath,
        (string Key, string Reason)[] allowlist)
    {
        var prod = ParseToml(prodPath);
        var staging = ParseToml(stagingPath);
        var allowed = allowlist.ToDictionary(e => e.Key, e => e.Reason, StringComparer.Ordinal);

        var allKeys = prod.Keys.Union(staging.Keys, StringComparer.Ordinal);
        var unexpected = new List<string>();

        foreach (var key in allKeys)
        {
            prod.TryGetValue(key, out var prodValue);
            staging.TryGetValue(key, out var stagingValue);
            if (string.Equals(prodValue, stagingValue, StringComparison.Ordinal)) continue;
            if (allowed.ContainsKey(key)) continue;

            unexpected.Add(
                $"{key}: {Path.GetFileName(prodPath)}={prodValue ?? "<absent>"}, " +
                $"{Path.GetFileName(stagingPath)}={stagingValue ?? "<absent>"}");
        }

        Assert.True(
            unexpected.Count == 0,
            $"{Path.GetFileName(prodPath)} and {Path.GetFileName(stagingPath)} diverge on a key that " +
            "is not in this test's allowlist. Either mirror the change to the other file, or add the " +
            "key to the allowlist here with a one-line reason it's intended:\n" +
            string.Join("\n", unexpected.Select(u => $"  {u}")));
    }

    [Fact]
    public void Backend_prod_and_staging_differ_only_on_the_allowlisted_keys()
    {
        AssertOnlyAllowlistedKeysDiffer(BackendProd, BackendStaging, BackendAllowlist);
    }

    [Fact]
    public void Ui_prod_and_staging_differ_only_on_the_allowlisted_keys()
    {
        AssertOnlyAllowlistedKeysDiffer(UiProd, UiStaging, UiAllowlist);
    }

    [Fact]
    public void Metrics_table_form_does_not_matter_for_parity()
    {
        // The concrete symptom C-9-6 named: fly.backend.toml uses `[metrics]` (a table),
        // fly.backend.staging.toml uses `[[metrics]]` (an array of tables). Both are accepted by
        // Fly and both currently carry the identical two keys; this pins that the parser folds them
        // onto the same key names rather than silently comparing nothing.
        var prod = ParseToml(BackendProd);
        var staging = ParseToml(BackendStaging);

        Assert.True(prod.ContainsKey("metrics.port"), "fly.backend.toml's [metrics] should parse to metrics.port");
        Assert.True(staging.ContainsKey("metrics.port"), "fly.backend.staging.toml's [[metrics]] should also parse to metrics.port");
        Assert.Equal(prod["metrics.port"], staging["metrics.port"]);
        Assert.Equal(prod["metrics.path"], staging["metrics.path"]);
    }
}
