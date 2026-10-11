namespace KiriChess.Server.Analytics;

/// <summary>One counter row, flattened out of EF.</summary>
public readonly record struct DailyCounterFact(DateOnly Date, string Key, string Source, long Count);

/// <summary>
/// Turns raw counters and three flat lists of timestamps into the AD.6 / 11a.3 traffic summary. Pure
/// and synchronous for the same reason BalanceAnalytics is: the maths is the part worth testing, and
/// keeping it out of the query means it is tested with a literal list instead of a Postgres container.
///
/// Games arrive as timestamps rather than as counters because they are already stored authoritatively
/// in the Games table - a beacon re-counting them would be less accurate AND blockable (README
/// ground-truth correction 1).
///
/// Zero-fills days with no rows: a gap in the series is a day nobody visited, which is a data point,
/// and a chart that simply omits it lies about the shape of the trend.
/// </summary>
public static class TrafficAnalytics
{
    public static TrafficSummaryDto Summarize(
        IReadOnlyList<DailyCounterFact> counters,
        IReadOnlyList<DateTimeOffset> signupsAt,
        IReadOnlyList<DateTimeOffset> gamesStartedAt,
        IReadOnlyList<DateTimeOffset> gamesFinishedAt,
        int sinceDays,
        DateOnly today,
        bool truncated,
        DateTimeOffset generatedAt)
    {
        var from = today.AddDays(-(sinceDays - 1));

        // A counter dated outside the window is ignored rather than treated as an error - the caller
        // owns the query bound, this function only owns the maths.
        var inWindow = counters.Where(c => c.Date >= from && c.Date <= today).ToList();
        var pageCounters = inWindow.Where(c => CounterKeys.Pages.Contains(c.Key)).ToList();

        long totalPageViews = pageCounters.Sum(c => c.Count);

        var byPage = pageCounters
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Select(g => new CounterCountDto(g.Key, g.Sum(c => c.Count)))
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        var bySource = BuildBySource(inWindow);

        long landingViews = pageCounters.Where(c => c.Key == CounterKeys.Landing).Sum(c => c.Count);
        int totalSignups = signupsAt.Count;
        // Never called a conversion rate (see TrafficSummaryDto's doc comment): views cannot be
        // deduplicated, so this is signups per hundred VIEWS, not per hundred people.
        double? signupsPerHundredLandingViews = landingViews == 0
            ? null
            : Math.Round(100.0 * totalSignups / landingViews, 1);

        var days = BuildDays(inWindow, signupsAt, gamesStartedAt, gamesFinishedAt, from, today);
        var firstSession = BuildFirstSession(inWindow, gamesStartedAt, gamesFinishedAt);

        return new TrafficSummaryDto(
            generatedAt, sinceDays, totalPageViews, totalSignups, signupsPerHundredLandingViews,
            truncated, byPage, bySource, firstSession, days);
    }

    /// <summary>
    /// A source with zero page views but a non-zero signup counter still gets a row - dropping it
    /// would hide a conversion. <see cref="CounterSources.Direct"/> is included like any other.
    /// </summary>
    private static IReadOnlyList<SourceCountDto> BuildBySource(IReadOnlyList<DailyCounterFact> inWindow)
    {
        var pageViewsBySource = inWindow
            .Where(c => CounterKeys.Pages.Contains(c.Key))
            .GroupBy(c => c.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.Ordinal);

        var signupsBySource = inWindow
            .Where(c => c.Key == CounterKeys.Signup)
            .GroupBy(c => c.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.Ordinal);

        var sources = pageViewsBySource.Keys
            .Union(signupsBySource.Keys, StringComparer.Ordinal);

        return sources
            .Select(s => new SourceCountDto(
                s,
                pageViewsBySource.GetValueOrDefault(s, 0L),
                signupsBySource.GetValueOrDefault(s, 0L)))
            .OrderByDescending(d => d.PageViews)
            .ThenBy(d => d.Source, StringComparer.Ordinal)
            .ToList();
    }

    private static List<TrafficDayDto> BuildDays(
        IReadOnlyList<DailyCounterFact> inWindow,
        IReadOnlyList<DateTimeOffset> signupsAt,
        IReadOnlyList<DateTimeOffset> gamesStartedAt,
        IReadOnlyList<DateTimeOffset> gamesFinishedAt,
        DateOnly from,
        DateOnly today)
    {
        var countersByDate = inWindow.ToLookup(c => c.Date);
        // UTC, never local - DateTimeOffset.UtcDateTime is the instant's own UTC clock reading.
        var signupsByDate = signupsAt.ToLookup(t => DateOnly.FromDateTime(t.UtcDateTime));
        var startedByDate = gamesStartedAt.ToLookup(t => DateOnly.FromDateTime(t.UtcDateTime));
        var finishedByDate = gamesFinishedAt.ToLookup(t => DateOnly.FromDateTime(t.UtcDateTime));

        var days = new List<TrafficDayDto>();
        for (var date = from; date <= today; date = date.AddDays(1))
        {
            var pages = countersByDate[date]
                .Where(c => CounterKeys.Pages.Contains(c.Key))
                .GroupBy(c => c.Key, StringComparer.Ordinal)
                .Select(g => new CounterCountDto(g.Key, g.Sum(c => c.Count)))
                .OrderByDescending(d => d.Count)
                .ThenBy(d => d.Key, StringComparer.Ordinal)
                .ToList();

            days.Add(new TrafficDayDto(
                date,
                pages.Sum(p => p.Count),
                signupsByDate[date].Count(),
                startedByDate[date].Count(),
                finishedByDate[date].Count(),
                pages));
        }

        return days;
    }

    private static FirstSessionDto BuildFirstSession(
        IReadOnlyList<DailyCounterFact> inWindow,
        IReadOnlyList<DateTimeOffset> gamesStartedAt,
        IReadOnlyList<DateTimeOffset> gamesFinishedAt)
    {
        long Sum(string key) => inWindow.Where(c => c.Key == key).Sum(c => c.Count);

        long learnStarted = Sum(CounterKeys.Learn);
        long learnCompleted = Sum(CounterKeys.LearnComplete);
        long learnSkipped = Sum(CounterKeys.LearnSkip);
        long queueJoined = Sum(CounterKeys.QueueJoin);
        long queueMatched = Sum(CounterKeys.QueueMatch);
        long gamesStarted = gamesStartedAt.Count;
        long gamesFinished = gamesFinishedAt.Count;

        double? learnCompletionPercent = learnStarted == 0
            ? null : Math.Round(100.0 * learnCompleted / learnStarted, 1);
        double? queueMatchPercent = queueJoined == 0
            ? null : Math.Round(100.0 * queueMatched / queueJoined, 1);
        // Deliberately NOT clamped to 100: a game started before the window can finish inside it, and
        // clamping would disguise that boundary artefact as a fact.
        double? gameFinishPercent = gamesStarted == 0
            ? null : Math.Round(100.0 * gamesFinished / gamesStarted, 1);

        return new FirstSessionDto(
            learnStarted, learnCompleted, learnSkipped, queueJoined, queueMatched,
            gamesStarted, gamesFinished, learnCompletionPercent, queueMatchPercent, gameFinishPercent);
    }
}
