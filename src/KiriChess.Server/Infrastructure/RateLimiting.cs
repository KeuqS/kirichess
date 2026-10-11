using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using KiriChess.Server.Auth;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Infrastructure;

/// <summary>Policy names shared between registration and the endpoints that opt in.</summary>
public static class RateLimitPolicies
{
    /// <summary>Credential and email-sending endpoints, partitioned by client IP, since the caller is by definition not yet authenticated.</summary>
    public const string Auth = "auth";

    /// <summary>Game creation, partitioned by user. Each call inserts a row, so this is the cheapest write to abuse.</summary>
    public const string GameCreate = "game-create";

    /// <summary>Move submission over HTTP. Deliberately the loosest policy in the file; see the header comment on <see cref="RateLimitingSetup"/>.</summary>
    public const string GameMove = "game-move";

    /// <summary>Report submission (Phase 6e.2). Defined here, applied by the integrator to Task B's endpoints.</summary>
    public const string Reports = "reports";

    /// <summary>Public replay reads (Phase 8.2), partitioned by IP, since the caller is anonymous by design.</summary>
    public const string Replay = "replay";

    /// <summary>Display-name changes (Phase 8a.4), partitioned by user. The cooldown is the product
    /// rule; this is only the anti-script backstop.</summary>
    public const string DisplayName = "display-name";

    /// <summary>Export and erasure (Phase 8a.2), partitioned by user. Both are expensive and neither
    /// has any legitimate high-frequency use. Routine profile reads and settings writes live on
    /// <see cref="AccountManage"/> so a few /profile visits cannot exhaust this budget.</summary>
    public const string AccountData = "account-data";

    /// <summary>
    /// Routine signed-in account surface: sessions, notification prefs, email status, avatar, push
    /// subscribe. Partitioned by user. Separate from <see cref="AccountData"/> so loading /profile
    /// (several cheap GETs) cannot burn the five-per-hour export/erasure budget, and so flipping a
    /// mail toggle cannot make "Download your data" return 429.
    /// </summary>
    public const string AccountManage = "account-manage";

    /// <summary>
    /// $1-4: minting a donation reference and reading the caller's own badge. Partitioned by user.
    /// Separate from <see cref="AccountData"/> so looking at /donate cannot exhaust the export and
    /// erasure budget, and so a donor who trips this limit is not silently sent to pay without a
    /// reference.
    /// </summary>
    public const string Donations = "donations";

    /// <summary>Two-factor enrolment and management (Phase 9d.2) - partitioned by user. Looser than
    /// AccountData because one enrolment is several calls, tighter than nothing because each verify
    /// attempt is a guess at a six-digit code.</summary>
    public const string TwoFactor = "two-factor";

    /// <summary>Challenge creation (Phase 9d.8) - partitioned by user. 6e.3: each call mints an
    /// invitation, and an invitation is the thing a spammer wants to mass-produce.</summary>
    public const string ChallengeCreate = "challenge-create";

    /// <summary>AD.6 / 11a.3: the anonymous counter beacon.</summary>
    public const string Counter = "analytics-counter";

    /// <summary>Phase 9f.5: public/bot API reads, partitioned by API token id.</summary>
    public const string ApiRead = "api-read";

    /// <summary>Phase 9f.5: public/bot API game and challenge creation, partitioned by API token id.</summary>
    public const string ApiPlayCreate = "api-play-create";

    /// <summary>Phase 9f.5: public/bot API HTTP moves. Same numbers as <see cref="GameMove"/>.</summary>
    public const string ApiPlayMove = "api-play-move";

    /// <summary>Phase 9f.5: first-party API-token mint/list/revoke, partitioned by user. Separate
    /// from <see cref="AccountData"/> so filling the ten-token cap is possible in one sitting
    /// without sharing the export/erasure budget.</summary>
    public const string ApiTokenManage = "api-token-manage";

    /// <summary>
    /// S-4-2: the one per-<em>account</em> ceiling over the three per-token policies above.
    /// Partitioned by user, and applied by the global limiter rather than by
    /// <c>RequireRateLimiting</c> - see <see cref="RateLimitingSetup"/>'s global-limiter block for
    /// why it cannot be a group convention.
    /// </summary>
    public const string ApiAccount = "api-account";
}

/// <summary>
/// Every tunable for both the HTTP rate limiter (<see cref="RateLimitingSetup"/>) and the SignalR
/// hub limiter (<see cref="HubRateLimitFilter"/>), read from configuration in one place so a deploy
/// can loosen any threshold without a rebuild. Follows the same shape as
/// <c>MetricsOptions</c> in <c>Infrastructure/MetricsEndpoint.cs</c>: a plain record with a
/// <see cref="From"/> factory, meant to be constructed after <c>builder.Build()</c> (or inside a
/// callback the framework itself defers until then) rather than off <c>builder.Configuration</c>
/// eagerly, see the README's "never read config eagerly" rule and the 6a.8 post-mortem it cites.
/// </summary>
public sealed record RateLimitOptions(
    bool Enabled,
    int AuthPermitsPerMinute,
    int GameCreatePermitsPerMinute,
    int GameMoveBurstTokens,
    int GameMoveTokensPerSecond,
    int ReportsPermitsPerHour,
    int GlobalPermitsPerMinute,
    int HubMoveBurst,
    int HubMoveTokensPerSecond,
    int HubQueueBurst,
    int HubQueueTokensPerSecond,
    int HubDefaultBurst,
    int HubDefaultTokensPerSecond,
    int ReplayPermitsPerMinute,
    bool Distributed,
    int DisplayNamePermitsPerDay,
    int AccountDataPermitsPerHour,
    int AccountManagePermitsPerMinute,
    int DonationsPermitsPerHour,
    int TwoFactorPermitsPerHour,
    int ChallengeCreatePermitsPerMinute,
    int CounterPermitsPerMinute,
    int ApiReadPermitsPerMinute,
    int ApiPlayCreatePermitsPerMinute,
    int ApiPlayMoveBurstTokens,
    int ApiPlayMoveTokensPerSecond,
    int ApiTokenManagePermitsPerHour,
    int ApiAccountPermitsPerMinute)
{
    public static RateLimitOptions From(IConfiguration configuration) => new(
        configuration.GetValue("RateLimiting:Enabled", true),
        configuration.GetValue("RateLimiting:Auth:PermitsPerMinute", 30),
        configuration.GetValue("RateLimiting:GameCreate:PermitsPerMinute", 12),
        configuration.GetValue("RateLimiting:GameMove:BurstTokens", 60),
        configuration.GetValue("RateLimiting:GameMove:TokensPerSecond", 30),
        configuration.GetValue("RateLimiting:Reports:PermitsPerHour", 20),
        configuration.GetValue("RateLimiting:Global:PermitsPerMinute", 600),
        configuration.GetValue("RateLimiting:Hub:MoveBurst", 60),
        configuration.GetValue("RateLimiting:Hub:MoveTokensPerSecond", 30),
        configuration.GetValue("RateLimiting:Hub:QueueBurst", 10),
        configuration.GetValue("RateLimiting:Hub:QueueTokensPerSecond", 1),
        configuration.GetValue("RateLimiting:Hub:DefaultBurst", 30),
        configuration.GetValue("RateLimiting:Hub:DefaultTokensPerSecond", 3),
        configuration.GetValue("RateLimiting:Replay:PermitsPerMinute", 60),
        // Phase 8.4: an escape hatch, not a feature toggle. With Redis configured the HTTP policies
        // below default to sharing their counters across instances; if that distributed path ever
        // misbehaves under real traffic, this flips every HTTP policy back to today's per-instance
        // limiter with one env var, while leaving Redis attached for the backplane, ownership and
        // everything else. Defaults true so a Redis deploy gets the correct multi-instance behaviour
        // without a second setting to remember, an escape hatch nobody knows exists is not one.
        configuration.GetValue("RateLimiting:Distributed", true),
        // Phase 8a.4: the anti-script backstop behind the rename endpoint. The product-level cooldown
        // (ContentModeration:RenameCooldownDays) is the real control; this only stops a script hammering
        // the endpoint within a single day.
        configuration.GetValue("RateLimiting:DisplayName:PermitsPerDay", 5),
        // Phase 8a.2: export and erasure. Both are expensive (export walks several tables; erasure
        // opens a transaction) and neither has any legitimate reason to be called more than a
        // handful of times an hour by the same account. Keep this tight; do not hang profile GETs
        // on it - those belong on AccountManage.
        configuration.GetValue("RateLimiting:AccountData:PermitsPerHour", 5),
        // Profile settings: /profile alone fires email + notifications + sessions (three GETs),
        // and React Strict Mode can double that in development. Per-minute, not per-hour, so a
        // normal visit never trips; 60/min still bounds a scripted session-revoke or avatar loop.
        configuration.GetValue("RateLimiting:AccountManage:PermitsPerMinute", 60),
        // $1-4: donation reference mint and own-badge read. A signed-in visit to /donate plus the
        // profile badge is two calls, and the mint reuses an unconsumed row, so this is a bound on
        // looking, not on paying. 30/hour is well above any real donor and well below sharing the
        // five-per-hour export/erasure budget.
        configuration.GetValue("RateLimiting:Donations:PermitsPerHour", 30),
        // Phase 9d.2: 2FA enrolment and management. Looser than AccountData - a single enrolment is
        // status + enrol + enable, already three calls - but not unlimited, since enable/disable/
        // recovery-codes are all a guess at a six-digit code.
        configuration.GetValue("RateLimiting:TwoFactor:PermitsPerHour", 20),
        // Phase 9d.8: same shape as GameCreate - each call inserts a row, and a challenge link is an
        // invitation a spammer wants to mass-produce (6e.3).
        configuration.GetValue("RateLimiting:ChallengeCreate:PermitsPerMinute", 10),
        // AD.6: the beacon is anonymous and IP-partitioned, so this is the only thing standing between a
        // script and an unbounded number of increments to one counter. Generous in absolute terms - a real
        // visitor clicking through the site fires a handful per minute, and one page can fire two (a page
        // key and a funnel key) - but low enough that inflating a number by hand is tedious. It protects
        // the DATA's usefulness more than it protects the server.
        configuration.GetValue("RateLimiting:Counter:PermitsPerMinute", 60),
        // Phase 9f.5: bot reads, keyed by token. More generous than the anonymous replay policy
        // (60/min IP) because a polling engine is a legitimate client. Moves stay at the human
        // GameMove numbers, a key must not buy a faster clock.
        configuration.GetValue("RateLimiting:ApiRead:PermitsPerMinute", 300),
        configuration.GetValue("RateLimiting:ApiPlayCreate:PermitsPerMinute", 12),
        configuration.GetValue("RateLimiting:ApiPlayMove:BurstTokens", 60),
        configuration.GetValue("RateLimiting:ApiPlayMove:TokensPerSecond", 30),
        // Phase 9f.5: minting a token is cheap compared to export/erasure, and the product cap is
        // ten active tokens. 20/hour lets a user fill (or rotate) that cap without waiting, and
        // still bounds a scripted mint loop.
        configuration.GetValue("RateLimiting:ApiTokenManage:PermitsPerHour", 20),
        // S-4-2: the ceiling over the three per-token policies. An account may hold ten active tokens
        // (ApiTokenDefaults.MaxActivePerUser), and per-token partitioning - which is the right
        // primitive and stays - meant its real budget was ten times every published number: 3000
        // reads a minute rather than 300. /api/v1 is also exempt from the per-IP global backstop, so
        // nothing sat underneath to catch it.
        //
        // 1800 is derived, not picked. It is exactly ApiPlayMove:TokensPerSecond x 60, i.e. one
        // token's own sustained move rate, so this ceiling can never be the thing that rejects a move
        // for a single-token client - which is this file's governing rule (a rejected move can lose a
        // game), and the reason it is not set anywhere near one token's read budget. Ten tokens now
        // buy 1800 requests a minute between them instead of 3000 reads each.
        //
        // What it does not bind: game creation. Ten tokens still buy 120 POST /api/v1/games a minute,
        // because a ceiling low enough to bite that would be far below one token's move rate. That
        // needs its own per-user create policy, not a smaller number here.
        configuration.GetValue("RateLimiting:ApiAccount:PermitsPerMinute", 1800));
}

/// <summary>
/// Registers and wires the ASP.NET Core rate limiter for the REST surface (Phase 6e.1).
/// <c>Microsoft.AspNetCore.RateLimiting</c> and <c>System.Threading.RateLimiting</c> ship in the
/// shared framework for <c>Microsoft.NET.Sdk.Web</c> projects on net10.0, no package reference
/// needed.
///
/// <b>The governing rule for every threshold here: a rejected move can lose a game.</b>
/// <c>GameSession</c> charges think time from <c>LastMoveAtUtc</c>; if the server refuses a move the
/// player's clock keeps running and they can flag while a script, not a human, was the reason the
/// request was ever rejected. So the move policy is set far above any plausible human or premove
/// cadence, every partition uses <c>QueueLimit = 0</c> (queuing a move behind a limiter is strictly
/// worse than rejecting it fast: the clock runs either way), and when in doubt the correct direction
/// for any number in this file is looser, not tighter.
/// </summary>
public static class RateLimitingSetup
{
    public static IServiceCollection AddKiriChessRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // services.AddRateLimiter(Action<RateLimiterOptions>), the overload every policy below used
        // to be registered through, expands to exactly services.AddRateLimiter() (the framework's own
        // metrics wiring) plus services.Configure(configureOptions). It is split into those two calls
        // here because the second one needs to run through the DI-aware OptionsBuilder.Configure
        // overload instead: the plain Action<RateLimiterOptions> callback has no way to receive a
        // constructor-injected dependency, and Step 2 of the Phase 8.4 distributed-rate-limiting plan
        // needs IRedisConnection inside this callback. This is not a reduced registration, it is the
        // same one, built from its two halves so the second half can ask for one.
        services.AddRateLimiter();

        services.AddOptions<RateLimiterOptions>().Configure<IRedisConnection, ILogger<RedisRateLimiter>>((o, redis, redisLimiterLogger) =>
        {
            // This callback runs the first time the framework resolves RateLimiterOptions, after
            // the host finishes Build(), not synchronously when AddOptions/Configure is called here.
            // So reading `configuration` (the same instance as app.Configuration) inside it is safe:
            // it sees the fully composed configuration, including any WebApplicationFactory test
            // override, the same rule the 6a.8 post-mortem exists for. Do not hoist this read above
            // the lambda. `redis` is resolved from the real service provider at that same
            // first-resolution point, once, not per request, exactly like `options` below, because
            // OptionsBuilder.Configure<TDep> only re-invokes this callback if RateLimiterOptions is
            // ever recomputed, which nothing here triggers.
            var options = RateLimitOptions.From(configuration);
            // Effective gate: Redis must be configured at all, and the escape hatch must not have
            // been flipped off. Either one being false reproduces today's exact in-memory behaviour,
            // per the README's fail-closed rule, a deploy with no Redis pays nothing extra to read
            // this, and a deploy with Redis can turn distributed limiting off without losing Redis
            // for the backplane, ownership or anything else.
            var distributed = redis.Enabled && options.Distributed;

            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy(RateLimitPolicies.Auth, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.Auth, IpKey(httpContext), redis, redisLimiterLogger,
                        options.AuthPermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(IpKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.AuthPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.GameCreate, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.GameCreate, PartitionKey(httpContext), redis, redisLimiterLogger,
                        options.GameCreatePermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.GameCreatePermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.GameMove, httpContext =>
                distributed
                    ? RedisRateLimitPartition.TokenBucket(RateLimitPolicies.GameMove, PartitionKey(httpContext), redis, redisLimiterLogger,
                        options.GameMoveBurstTokens, options.GameMoveTokensPerSecond)
                    : RateLimitPartition.GetTokenBucketLimiter(PartitionKey(httpContext), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.GameMoveBurstTokens,
                        TokensPerPeriod = options.GameMoveTokensPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            o.AddPolicy(RateLimitPolicies.Reports, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.Reports, PartitionKey(httpContext), redis, redisLimiterLogger,
                        options.ReportsPermitsPerHour, TimeSpan.FromHours(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ReportsPermitsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.Replay, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.Replay, IpKey(httpContext), redis, redisLimiterLogger,
                        options.ReplayPermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(IpKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ReplayPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.DisplayName, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.DisplayName, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.DisplayNamePermitsPerDay, TimeSpan.FromDays(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.DisplayNamePermitsPerDay,
                        Window = TimeSpan.FromDays(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.AccountData, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.AccountData, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.AccountDataPermitsPerHour, TimeSpan.FromHours(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.AccountDataPermitsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.AccountManage, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.AccountManage, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.AccountManagePermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.AccountManagePermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.Donations, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.Donations, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.DonationsPermitsPerHour, TimeSpan.FromHours(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.DonationsPermitsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.Counter, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.Counter, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.CounterPermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.CounterPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.ApiTokenManage, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.ApiTokenManage, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.ApiTokenManagePermitsPerHour, TimeSpan.FromHours(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ApiTokenManagePermitsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.TwoFactor, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.TwoFactor, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.TwoFactorPermitsPerHour, TimeSpan.FromHours(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.TwoFactorPermitsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.ChallengeCreate, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.ChallengeCreate, PartitionKey(httpContext),
                        redis, redisLimiterLogger, options.ChallengeCreatePermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ChallengeCreatePermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.ApiRead, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.ApiRead, TokenKey(httpContext), redis, redisLimiterLogger,
                        options.ApiReadPermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(TokenKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ApiReadPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.ApiPlayCreate, httpContext =>
                distributed
                    ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.ApiPlayCreate, TokenKey(httpContext), redis, redisLimiterLogger,
                        options.ApiPlayCreatePermitsPerMinute, TimeSpan.FromMinutes(1))
                    : RateLimitPartition.GetFixedWindowLimiter(TokenKey(httpContext), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.ApiPlayCreatePermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            o.AddPolicy(RateLimitPolicies.ApiPlayMove, httpContext =>
                distributed
                    ? RedisRateLimitPartition.TokenBucket(RateLimitPolicies.ApiPlayMove, TokenKey(httpContext), redis, redisLimiterLogger,
                        options.ApiPlayMoveBurstTokens, options.ApiPlayMoveTokensPerSecond)
                    : RateLimitPartition.GetTokenBucketLimiter(TokenKey(httpContext), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.ApiPlayMoveBurstTokens,
                        TokensPerPeriod = options.ApiPlayMoveTokensPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            // Every partition above, Redis-backed or in-memory, keeps QueueLimit = 0: there is no
            // queueing anywhere in this file. Queuing a move behind a limiter is strictly worse than
            // rejecting it fast, because the clock runs either way; the distributed path does not
            // change that reasoning; it just changes where the counter lives.

            // Global backstop across every request, partitioned by IP. `/health` and `/metrics` are
            // exempted: an orchestrator health probe or a Prometheus scrape must never be throttled.
            // `/metrics` also has its own gate (MetricsEndpoint.UseMetricsScrapeGuard), this is
            // belt-and-braces, not a substitute for it. Skipped entirely when both limits are <= 0.
            //
            // S-4-2 lives here rather than as a `RequireRateLimiting` convention on the /api/v1 group
            // builders, and that is not a stylistic choice. The rate-limiting middleware reads one
            // EnableRateLimitingAttribute off the endpoint (`Metadata.GetMetadata<T>()` returns the
            // last item of that type), so a group-level policy is *replaced* by the endpoint's own
            // ApiRead/ApiPlayCreate/ApiPlayMove policy rather than composed with it: the ceiling would
            // silently never run. The global limiter is the one layer that is documented to apply in
            // addition to the endpoint's policy, which is exactly the "one ceiling above the
            // partitions" shape the finding asks for.
            if (options.GlobalPermitsPerMinute > 0 || options.ApiAccountPermitsPerMinute > 0)
            {
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    var path = httpContext.Request.Path;
                    if (path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics")
                        || path.StartsWithSegments("/openapi"))
                        return RateLimitPartition.GetNoLimiter("exempt");

                    // /api/v1 stays exempt from the per-IP backstop - a bot behind one NAT is not the
                    // population that limit is about - but it is no longer exempt from everything.
                    // The key carries its own `apiv1:` prefix so an anonymous (and therefore
                    // IP-keyed) /api/v1 request cannot land in the same partition as the per-IP
                    // branch below, which is cached by key and would otherwise hand it the wrong
                    // permit count.
                    if (path.StartsWithSegments("/api/v1"))
                    {
                        if (options.ApiAccountPermitsPerMinute <= 0)
                            return RateLimitPartition.GetNoLimiter("exempt");

                        var accountKey = $"apiv1:{PartitionKey(httpContext)}";
                        return distributed
                            ? RedisRateLimitPartition.FixedWindow(RateLimitPolicies.ApiAccount, accountKey, redis, redisLimiterLogger,
                                options.ApiAccountPermitsPerMinute, TimeSpan.FromMinutes(1))
                            : RateLimitPartition.GetFixedWindowLimiter(accountKey, _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = options.ApiAccountPermitsPerMinute,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0
                            });
                    }

                    if (options.GlobalPermitsPerMinute <= 0)
                        return RateLimitPartition.GetNoLimiter("exempt");

                    return distributed
                        ? RedisRateLimitPartition.FixedWindow("global", IpKey(httpContext), redis, redisLimiterLogger,
                            options.GlobalPermitsPerMinute, TimeSpan.FromMinutes(1))
                        : RateLimitPartition.GetFixedWindowLimiter(IpKey(httpContext), _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.GlobalPermitsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                });
            }

            o.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                // MetadataName.RetryAfter is MetadataName<TimeSpan>, so the out value is strongly
                // typed, no cast, and no guessing at the metadata's runtime shape.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                // Endpoint-level policies stamp EnableRateLimitingAttribute metadata with the policy
                // name passed to RequireRateLimiting; the global limiter has no such metadata, so
                // anything without it fell to the global backstop instead of a named policy. Never
                // tag with a user id, IP or connection id here, see KiriChessMetrics' cardinality rule.
                var policyName = context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                var metrics = context.HttpContext.RequestServices.GetRequiredService<KiriChessMetrics>();
                metrics.RateLimitRejected("http", policyName ?? "global");

                context.HttpContext.Response.ContentType = "application/json";
                // Keep the message generic, do not leak which policy fired or what the limit is.
                await context.HttpContext.Response.WriteAsync(
                    "{\"error\":\"Too many requests. Try again shortly.\"}", cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Adds the rate-limiting middleware, but only when enabled, a disabled configuration adds no
    /// middleware to the pipeline at all, rather than a middleware that always passes.
    /// </summary>
    public static WebApplication UseKiriChessRateLimiter(this WebApplication app, RateLimitOptions options)
    {
        if (options.Enabled)
            app.UseRateLimiter();
        return app;
    }

    /// <summary>
    /// Partition key for the per-user policies: the caller's user id when authenticated, else their
    /// IP, else the literal "anonymous". <c>UseForwardedHeaders()</c> already runs first in the
    /// pipeline with <c>KnownProxies</c>/<c>KnownIPNetworks</c> cleared for Fly, so
    /// <c>RemoteIpAddress</c> is the real client by the time a limiter sees it, that is the whole
    /// reason IP partitioning is trustworthy here at all.
    /// </summary>
    internal static string PartitionKey(HttpContext context)
    {
        var raw = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
        return raw is not null ? $"user:{raw}" : IpKey(context);
    }

    /// <summary>Partition key for the IP-only policies (the caller is not authenticated yet).</summary>
    internal static string IpKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        return ip is null ? "anonymous" : $"ip:{ip}";
    }

    /// <summary>
    /// Partition key for the bot-API policies (Phase 9f.5): the token id when present, else the
    /// ordinary per-user/IP key. Named policies on <c>/api/v1</c> run after authentication, so the
    /// claim is populated for a well-formed request.
    /// </summary>
    internal static string TokenKey(HttpContext context)
    {
        var tokenId = context.User.FindFirst(ApiTokenClaims.TokenId)?.Value;
        return tokenId is not null ? $"token:{tokenId}" : PartitionKey(context);
    }
}
