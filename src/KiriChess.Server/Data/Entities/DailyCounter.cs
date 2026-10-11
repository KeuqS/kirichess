namespace KiriChess.Server.Data.Entities;

/// <summary>
/// Phase AD.6 / 11a.3. One counter per (UTC date, allowlisted key, allowlisted source). This is the
/// whole of what the site records about anonymous traffic and the first-session funnel: no cookie, no
/// identifier, no IP, no user agent, no referrer, no per-event row - see
/// AD.6 / 11a.3 and Phase 8a.5 scope decision 4 for why that is
/// load-bearing rather than merely frugal. Keeping this table identifier-free is what keeps it outside
/// GDPR entirely, which is what lets it ship with no consent banner and no lawful basis.
///
/// Individual sessions are not reconstructable from this and are not meant to be. The deliverable is
/// the shape of the cliff, not the person falling off it.
/// </summary>
public sealed class DailyCounter
{
    public DateOnly Date { get; set; }
    public string CounterKey { get; set; } = string.Empty;

    /// <summary>
    /// The campaign bucket from an allowlisted <c>?src=</c>, or <see cref="CounterSources.Direct"/>
    /// when none was known. Never a referrer string. Always Direct for server-recorded keys, which
    /// have no way to know and must not be given one (README scope decision 14).
    /// </summary>
    public string Source { get; set; } = string.Empty;

    public long Count { get; set; }
}
