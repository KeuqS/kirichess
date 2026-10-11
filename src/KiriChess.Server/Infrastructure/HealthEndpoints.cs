using System.Diagnostics;
using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Infrastructure;

/// <summary>Liveness and readiness probes for the reverse proxy, container HEALTHCHECK and Fly checks.</summary>
public static class HealthEndpoints
{
    // S-1-6 + I-5-4, solved together rather than separately: /health/ready must stay anonymous and
    // exempt from the rate limiter (RateLimiting.cs's global backstop skips /health entirely) because
    // an orchestrator probe cannot carry a credential and must never be throttled - but that is also
    // exactly what turns "check the database" into a free, unlimited lever against Neon's connection
    // budget if the check does real work per call. So the check does more per call (a real query, not
    // just CanConnectAsync, so a connectable-but-broken schema is caught) and is called less often:
    // the result is cached for a few seconds, so the true cost of any burst of anonymous callers is at
    // most one round trip per window, never one per request. This is a cache TTL, not a game clock, but
    // the same clock-step hazard realtime.md warns about (NTP correction, VM live-migration) applies to
    // "is this cached result still fresh" math, so it is measured with the monotonic Stopwatch clock,
    // not DateTime/UtcNow.
    private static readonly TimeSpan ReadyCacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One cache entry per database, not one for the process. In production this is a distinction
    /// without a difference, since a process talks to exactly one database. It matters in the test
    /// suite, where several <c>WebApplicationFactory</c> instances live in one process and each
    /// stands up its own Postgres container: a single static pair would let a factory whose database
    /// is deliberately unreachable answer from the cached "ready" of a factory next door, inside the
    /// same five-second window, and the test that noticed would fail somewhere else entirely and
    /// only sometimes. Keyed on the connection string because that is what actually identifies the
    /// database being asked about.
    /// </summary>
    private sealed class ReadyCacheEntry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Stamp = long.MinValue;
        public bool Value;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReadyCacheEntry> ReadyCaches = new();

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process is up and the pipeline responds. Deliberately touches nothing else,
        // a slow database must not get the container killed.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
           .AllowAnonymous()
           .WithName("HealthLive");

        // Readiness: safe to send traffic, i.e. the database is reachable and has the schema this
        // build expects. Redis is reported alongside it but never affects the status code: Redis down
        // only degrades this server to single-instance (Phase 8.4), which is strictly less severe than
        // a dead database, and a server that stops answering readiness probes over something that
        // survivable gets killed for no reason.
        app.MapGet("/health/ready", async (KiriChessDbContext db, IRedisConnection redis, CancellationToken ct) =>
        {
            var redisStatus = !redis.Enabled ? "disabled" : redis.IsConnected ? "up" : "down";
            return await DatabaseIsReadyAsync(db, ct)
                ? Results.Ok(new { status = "ready", redis = redisStatus })
                : Results.Json(new { status = "unavailable", redis = redisStatus }, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
           .AllowAnonymous()
           .WithName("HealthReady");

        return app;
    }

    /// <summary>
    /// I-5-4: touches a real table rather than only opening a connection, so a rollback, an aborted
    /// migration, or a connection string pointed at the wrong branch/database - all of which pass
    /// <c>CanConnectAsync</c> - report unready instead of taking traffic and 500ing on the first real
    /// query. <c>Users</c> (from the Identity base class) is picked because it always exists in a
    /// migrated schema and an indexed <c>AnyAsync</c> is not a heavier round trip than the
    /// connectivity probe it replaces.
    ///
    /// S-1-6: the result is cached in-process for <see cref="ReadyCacheTtl"/> so a burst of anonymous
    /// callers costs at most one query per window, single-flighted through the entry's own gate
    /// rather than one query per caller.
    /// </summary>
    private static async Task<bool> DatabaseIsReadyAsync(KiriChessDbContext db, CancellationToken ct)
    {
        // Null only for a provider with no connection string (the in-memory provider some tests use);
        // they all share one entry, which is correct, since they are all the same nothing.
        var key = db.Database.GetConnectionString() ?? string.Empty;
        var cache = ReadyCaches.GetOrAdd(key, _ => new ReadyCacheEntry());

        var now = Stopwatch.GetTimestamp();
        if (cache.Stamp != long.MinValue && Stopwatch.GetElapsedTime(cache.Stamp, now) < ReadyCacheTtl)
            return cache.Value;

        await cache.Gate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: whoever lost the race to acquire it may find the winner
            // already refreshed the cache, in which case there is nothing left to do.
            now = Stopwatch.GetTimestamp();
            if (cache.Stamp != long.MinValue && Stopwatch.GetElapsedTime(cache.Stamp, now) < ReadyCacheTtl)
                return cache.Value;

            bool ready;
            try
            {
                await db.Users.AnyAsync(ct);
                ready = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ready = false;
            }

            cache.Value = ready;
            cache.Stamp = Stopwatch.GetTimestamp();
            return ready;
        }
        finally
        {
            cache.Gate.Release();
        }
    }
}
