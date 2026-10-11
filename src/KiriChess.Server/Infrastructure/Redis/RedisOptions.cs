namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// Every tunable for the Phase 8.4 multi-instance features, read in one place so a deploy can move a
/// timeout without a rebuild. Same shape and the same deferred-read discipline as
/// <c>MetricsOptions</c> and <c>RateLimitOptions</c>: construct it after <c>Build()</c>, never off
/// <c>builder.Configuration</c> eagerly.
///
/// <b>An empty <see cref="Configuration"/> is the whole safety story of this phase.</b> Unset means
/// no connection is opened, no distributed component is registered, and every subsystem behaves
/// exactly as it did before 8.4, the same fail-closed pattern as <c>Metrics:ScrapeToken</c> (6d.3).
/// Nothing is deployed yet (DH.1/DH.2 are open), so that unconfigured path is the one every developer
/// and every CI run actually takes.
/// </summary>
public sealed record RedisOptions(
    string Configuration,
    string KeyPrefix,
    int InstanceTtlSeconds,
    int HeartbeatSeconds,
    int CommandTimeoutSeconds)
{
    public const string SectionName = "Redis";

    /// <summary>True when Redis is configured. Every distributed code path is gated on this.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Configuration);

    /// <summary>
    /// <see cref="InstanceTtlSeconds"/> must be comfortably larger than <see cref="HeartbeatSeconds"/>
    ///, the intent is 3x, because the gap between them is how long a dead instance's games stay
    /// unclaimable. Shrinking it toward equality makes a slow GC pause look like a dead instance,
    /// which would hand a live game to a second owner while the first is still very much alive.
    /// </summary>
    public static RedisOptions From(IConfiguration configuration) => new(
        configuration.GetValue("Redis:Configuration", "") ?? "",
        configuration.GetValue("Redis:KeyPrefix", "kirichess:") ?? "kirichess:",
        configuration.GetValue("Redis:InstanceTtlSeconds", 30),
        configuration.GetValue("Redis:HeartbeatSeconds", 10),
        configuration.GetValue("Redis:CommandTimeoutSeconds", 5));
}
