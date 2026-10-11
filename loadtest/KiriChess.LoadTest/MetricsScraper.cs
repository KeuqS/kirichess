using System.Globalization;
using System.Text;

namespace KiriChess.LoadTest;

/// <summary>One parsed Prometheus text-format sample: a metric name, its label set, and a value.</summary>
internal sealed record MetricSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>A full /metrics scrape at one point in time.</summary>
internal sealed record MetricsSnapshot(DateTimeOffset TakenAt, IReadOnlyList<MetricSample> Samples);

/// <summary>
/// Scrapes GET /metrics and parses the OpenTelemetry Prometheus exporter's text format.
///
/// The exact series names below were discovered by scraping a live instance before this parser was
/// written (Phase 9b.3 § 2.3), not guessed from the instrument names
/// in KiriChessMetrics.cs. The exporter's naming convention, confirmed by triggering one of each kind
/// of event and diffing the raw text:
///   - Counters get a "_total" suffix; a "{unit}" annotation (e.g. "{game}", "{move}") is dropped
///     entirely - "kirichess.games.started" (unit "{game}") -&gt; kirichess_games_started_total.
///   - Histograms with a real unit get that unit's Prometheus name appended ("ms" -&gt; "milliseconds"),
///     plus the standard _bucket/_sum/_count series -
///     "kirichess.move.apply.duration" -&gt; kirichess_move_apply_duration_milliseconds{_bucket,_sum,_count}.
///   - Observable gauges keep the dotted name verbatim with dots turned to underscores and no suffix -
///     "kirichess.games.live" -&gt; kirichess_games_live.
///   - Every series carries otel_scope_name="KiriChess.Server" in addition to its own tags.
/// See loadtest/README.md for the full list with the raw scrape lines that proved each one.
/// </summary>
internal static class MetricsScraper
{
    public static async Task<MetricsSnapshot> ScrapeAsync(HttpClient http, string api, string scrapeToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{api}/metrics");
        if (!string.IsNullOrEmpty(scrapeToken))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", scrapeToken);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct);
        return new MetricsSnapshot(DateTimeOffset.UtcNow, ParseText(text));
    }

    internal static IReadOnlyList<MetricSample> ParseText(string text)
    {
        var samples = new List<MetricSample>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            string name;
            Dictionary<string, string> labels;
            string valuePart;

            var braceIdx = line.IndexOf('{');
            if (braceIdx >= 0)
            {
                name = line[..braceIdx];
                var closeIdx = line.IndexOf('}', braceIdx);
                if (closeIdx < 0) continue; // malformed; skip rather than throw on one bad line
                labels = ParseLabels(line[(braceIdx + 1)..closeIdx]);
                valuePart = line[(closeIdx + 1)..].Trim();
            }
            else
            {
                var spaceIdx = line.IndexOf(' ');
                if (spaceIdx < 0) continue;
                name = line[..spaceIdx];
                labels = new Dictionary<string, string>();
                valuePart = line[(spaceIdx + 1)..].Trim();
            }

            // A value line can carry a trailing timestamp ("value timestamp"); Prometheus exposition
            // format separates them by whitespace, and OTel's exporter here does not emit a
            // timestamp, but tolerate it anyway.
            var firstToken = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstToken is null) continue;
            if (!TryParseValue(firstToken, out var value)) continue;

            samples.Add(new MetricSample(name, labels, value));
        }
        return samples;
    }

    private static bool TryParseValue(string token, out double value)
    {
        if (token == "+Inf") { value = double.PositiveInfinity; return true; }
        if (token == "-Inf") { value = double.NegativeInfinity; return true; }
        if (token == "NaN") { value = double.NaN; return true; }
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static Dictionary<string, string> ParseLabels(string body)
    {
        var labels = new Dictionary<string, string>();
        var i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && (body[i] == ',' || body[i] == ' ')) i++;
            if (i >= body.Length) break;

            var eq = body.IndexOf('=', i);
            if (eq < 0) break;
            var key = body[i..eq].Trim();

            var j = eq + 1;
            if (j >= body.Length || body[j] != '"') break;
            j++;
            var sb = new StringBuilder();
            while (j < body.Length && body[j] != '"')
            {
                if (body[j] == '\\' && j + 1 < body.Length) { sb.Append(body[j + 1]); j += 2; }
                else { sb.Append(body[j]); j++; }
            }
            labels[key] = sb.ToString();
            i = j + 1;
        }
        return labels;
    }

    /// <summary>Sum of a counter's value across every label combination (or a filtered subset) at one snapshot.</summary>
    public static double CounterValue(MetricsSnapshot snap, string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null) =>
        snap.Samples.Where(s => s.Name == name && (filter is null || filter(s.Labels))).Sum(s => s.Value);

    /// <summary>Counter delta between two snapshots of the same series (end - start). Never negative in
    /// a healthy run; a negative delta means the process restarted between scrapes.</summary>
    public static double CounterDelta(MetricsSnapshot start, MetricsSnapshot end, string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null) =>
        CounterValue(end, name, filter) - CounterValue(start, name, filter);

    /// <summary>Current value of a single-series gauge (no tags, or already filtered to one).</summary>
    public static double GaugeValue(MetricsSnapshot snap, string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null) =>
        snap.Samples.Where(s => s.Name == name && (filter is null || filter(s.Labels))).Sum(s => s.Value);

    /// <summary>
    /// Window statistics for a histogram, derived from the delta of the cumulative _bucket series
    /// between two snapshots. Percentiles are bucket-derived (README § Decision 4): the reported pXX
    /// is the smallest bucket's upper bound (le) whose cumulative count covers that fraction of the
    /// window's observations - "p99 &lt;= 250ms", never a true measured value.
    /// </summary>
    public static HistogramStats HistogramWindow(MetricsSnapshot start, MetricsSnapshot end, string baseName,
        Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
    {
        var sumDelta = CounterDelta(start, end, baseName + "_sum", filter);
        var countDelta = CounterDelta(start, end, baseName + "_count", filter);

        // Aggregate bucket deltas across every matching series (e.g. every "pool" tag) into one
        // combined histogram for the window, keyed by the bucket upper bound.
        var bucketName = baseName + "_bucket";
        var startBuckets = BucketTotals(start, bucketName, filter);
        var endBuckets = BucketTotals(end, bucketName, filter);

        var les = startBuckets.Keys.Union(endBuckets.Keys).OrderBy(x => x).ToArray();
        var deltas = new Dictionary<double, double>();
        foreach (var le in les)
        {
            startBuckets.TryGetValue(le, out var s);
            endBuckets.TryGetValue(le, out var e);
            deltas[le] = Math.Max(0, e - s); // guard: a process restart could make this negative
        }

        double? Percentile(double p)
        {
            if (countDelta <= 0) return null;
            var target = p * countDelta;
            foreach (var le in les)
                if (deltas[le] >= target) return le;
            // Only reachable if the window's own +Inf bucket is somehow missing from the scrape -
            // report "worse than every bucket we saw" rather than silently under-reporting.
            return double.PositiveInfinity;
        }

        return new HistogramStats(
            Count: countDelta,
            Mean: countDelta > 0 ? sumDelta / countDelta : null,
            P50: Percentile(0.50),
            P95: Percentile(0.95),
            P99: Percentile(0.99));
    }

    private static Dictionary<double, double> BucketTotals(MetricsSnapshot snap, string bucketName,
        Func<IReadOnlyDictionary<string, string>, bool>? filter)
    {
        var totals = new Dictionary<double, double>();
        foreach (var s in snap.Samples)
        {
            if (s.Name != bucketName) continue;
            if (filter is not null && !filter(s.Labels)) continue;
            if (!s.Labels.TryGetValue("le", out var leStr)) continue;
            var le = leStr == "+Inf" ? double.PositiveInfinity : double.Parse(leStr, CultureInfo.InvariantCulture);
            totals[le] = totals.GetValueOrDefault(le) + s.Value;
        }
        return totals;
    }
}

/// <summary>Window statistics for one histogram. P50/P95/P99 are bucket upper bounds (an upper-bound
/// estimate of the real percentile), or null if the window recorded zero observations.</summary>
internal sealed record HistogramStats(double Count, double? Mean, double? P50, double? P95, double? P99);
