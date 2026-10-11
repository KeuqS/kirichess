using System.Security.Claims;
using System.Text;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Tooling;
using KiriChess.Server.Account;
using KiriChess.Server.Analytics;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Email;
using KiriChess.Server.Infrastructure.OpenApi;
using KiriChess.Server.Infrastructure.Push;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Moderation;
using KiriChess.Server.Moderation.Content;
using KiriChess.Server.Moderation.Detection;
using KiriChess.Server.Ratings;
using KiriChess.Server.Retention;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.AspNetCore;

// --- Cost-vector export mode (Phase 10b.1) ---
// `dotnet run --project src/KiriChess.Server -- --export-cost-vectors config/cost-vectors.generated.json`
// writes the golden (position, move) -> economy vectors and exits, the same "deliberate offline step"
// shape as --export-openapi below. Unlike that one, this mode needs no web host at all -
// KiriChessEngine.Apply is pure and stateless - so it runs and exits before
// WebApplication.CreateBuilder(args) is even reached.
var exportCostVectorsPath = ReadArgValue(args, "--export-cost-vectors");
if (exportCostVectorsPath is not null)
{
    await CostVectorGenerator.WriteAsync(exportCostVectorsPath);
    Console.WriteLine($"Cost vectors written to {exportCostVectorsPath}; exiting without serving.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// --- OpenAPI export mode (Phase 9d.7) ---
// `dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json` writes the
// document and exits, the same "deliberate offline step, never a boot-time side effect" shape as
// --migrate below. The configuration overlay is what makes the output reproducible on any machine:
// the document must not depend on whether the developer running it happens to have a database or a
// Redis configured locally. (Before AD.6+11a.3, the overlay also carried a placeholder analytics
// bearer token, because MapAnalyticsEndpoints used to return early without one; the analytics
// surface is behind ModerationPolicies.Admin now, so that placeholder is gone.) The Postgres string
// points at a port nothing listens on, exactly like HealthEndpointTests: the host must boot without a
// database, and RunMigrationsOnStartup=false is what keeps it from trying.
var exportOpenApiPath = ReadArgValue(args, "--export-openapi");
if (exportOpenApiPath is not null)
    builder.Configuration.AddInMemoryCollection(OpenApiExport.DeterministicConfiguration);

// C-12-4: --migrate may use a different role from the long-lived process. ConnectionStrings:Migrator
// is the owner string. The serving process never takes this branch, so it keeps ConnectionStrings:Postgres
// even when that role has no DDL. Absent a migrator string, --migrate uses the serving string, which
// is every local and compose path.
if (exportOpenApiPath is null && args.Contains("--migrate"))
{
    var selected = ServingConnection.Select(
        builder.Configuration.GetConnectionString("Postgres"),
        builder.Configuration.GetConnectionString("Migrator"),
        migrating: true);
    if (!string.Equals(selected, builder.Configuration.GetConnectionString("Postgres"), StringComparison.Ordinal))
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = selected,
        });
    }
}

// --- Logging (Phase 6d.2) ---
// Outside Development, log one JSON object per event to stdout: Fly and Docker both capture stdout,
// and structured fields are what make a log searchable instead of greppable. Providers are cleared
// first because the default console provider is still attached and would print every event twice.
// IncludeScopes is the whole point, it is what emits the UserId/GameId scopes below.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
    });
}

// --- Exception tracking (Phase 6d.2) ---
// No DSN, no SDK: the integration is not registered at all rather than registered-and-disabled, so
// an unconfigured deploy has zero egress and zero startup cost. No Sentry project exists yet, this
// is wired exactly as far as it can be wired without one (cf. LoggingEmailSender for 6b.2).
var sentryDsn = builder.Configuration.GetValue<string?>("Sentry:Dsn");
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.Environment = builder.Configuration.GetValue("Sentry:Environment", builder.Environment.EnvironmentName);
        o.MinimumEventLevel = LogLevel.Error;
        o.SendDefaultPii = false;
        // Traces (Phase 9e.7) travel the OTLP path registered below, not Sentry's own automatic
        // span creation, hence UseOtlp() here (it flips DisableSentryTracing on to stop
        // SentryHttpMessageHandler/EF/DiagnosticSource from double-emitting the spans OpenTelemetry's
        // own instrumentation already produces) and no TracesSampleRate: that knob only controls the
        // deprecated Sentry.OpenTelemetry SpanProcessor bridge, sampling for the OTLP path lives on
        // the TracerProviderBuilder instead.
        o.UseOtlp();
    });
}

// --- OpenAPI (Phase 9d.7) ---
// The document is not decoration: docs/api/openapi.json is generated from it and ui/lib/api.generated.ts
// is generated from that, so this is the single wire contract both ends compile against. Two options
// matter here:
//   ShouldInclude   keeps /health, /health/ready and the Prometheus scrape endpoint out. They are
//                   operational routes with no client, and a generated TS client has no business
//                   knowing about them.
//   AddSchemaTransformer  restores the enum information the DTOs deliberately do not carry in their
//                   C# types - see StringEnumAttribute for why they do not - and, separately (Task C),
//                   strips the input-only "string" alternative .NET 10 adds to every numeric schema
//                   because JsonSerializerDefaults.Web allows reading a number from a digit string. That
//                   is a true fact about what the server accepts, not about what it emits, and describing
//                   whiteTime/moveCount/rating/etc. as `number | string` would make the generated
//                   TypeScript client strictly less precise than the number it replaces - see
//                   NumericSchemaTransformer for the full reasoning and the JsonNumberHandling.Strict
//                   alternative it rejects.
//   AddOperationTransformer  states the real oneOf for the one route with two success shapes
//                   (POST /api/auth/login, Phase 9d.2) - .Produces<T> cannot express it and would
//                   otherwise document only whichever declaration happened to come last.
builder.Services.AddOpenApi(options => ConfigureKiriChessOpenApi(options, publicDocument: false));
builder.Services.AddOpenApi("public", options => ConfigureKiriChessOpenApi(options, publicDocument: true));

// --- CORS ---
// Origins come from configuration so a deployed frontend can call the API; the lambda body runs
// after the host is built, so `Cors__AllowedOrigins__0`-style env vars are picked up.
const string FrontendCorsPolicy = "frontend";
builder.Services.AddCors(o => o.AddPolicy(FrontendCorsPolicy, p =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    p.WithOrigins(origins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials();   // required by SignalR's negotiate request
}));

// --- HSTS (I-7-4) ---
// SecurityHeadersSetup.UseKiriChessSecurityHeaders calls app.UseHsts() unconditionally, which
// without this AddHsts falls back to ASP.NET's own default HstsOptions: 30 days, no subdomains.
// api.kirichess.com is the host every bearer token is sent to (docs/decisions/AUTH.md § 1), and it
// was pinning for a thirtieth as long as ui.kirichess.com's 730-day max-age (ui/next.config.ts).
// Matching the two here does not by itself add HSTS to responses Cloudflare answers without
// reaching an origin (the site's own edge certificate setting is a Cloudflare dashboard change, an
// owner action, not something this file can set). No `Preload`: close to irreversible and buys a
// one-person host nothing the max-age bump does not.
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(730);
    o.IncludeSubDomains = true;
});

// --- Data ---
// EnableRetryOnFailure matters most once DH.2 puts Neon (serverless, drops idle connections, cold
// starts) behind this connection string, a transient failure here must not cost a finished rated
// game. It installs a retrying execution strategy, which EF then requires explicit transactions to
// be wrapped in via CreateExecutionStrategy().ExecuteAsync(...), see AccountDeletionService, the
// only explicit BeginTransactionAsync in this server. Phase 5's game/rating write is unaffected: it
// rides SaveChangesAsync's own implicit transaction, which the retry strategy already covers.
builder.Services.AddDbContext<KiriChessDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"), npgsql =>
    {
        npgsql.CommandTimeout(builder.Configuration.GetValue("Database:CommandTimeoutSeconds", 30));
        npgsql.EnableRetryOnFailure(
            maxRetryCount: builder.Configuration.GetValue("Database:MaxRetryCount", 5),
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
    }));

// --- Data Protection (Phase 10a.1) ---
// Unconditional, and that is the whole point of the item. The only AddDataProtection() call used to
// live inside the Redis branch, and production runs with Redis disabled, so the deployed app was on
// ASP.NET's default key ring: written to the container filesystem and gone with the machine. That
// ring protects Identity's email-confirmation and password-reset tokens (AddDefaultTokenProviders
// below) and the Google OAuth correlation cookie, so every deploy invalidated every link already
// sitting in somebody's inbox, and the recipient met an invalid-token error on a link this system
// had legitimately sent them minutes earlier.
//
// Postgres rather than the filesystem because it is the only store here that survives the machine,
// and it is already where every other restart-surviving decision in 6c landed. Registered BEFORE
// AddKiriChessRedis, deliberately: each PersistKeysTo* sets KeyManagementOptions.XmlRepository and
// the last registration wins, so a configured Redis still takes over as the multi-instance path it
// was written to be, and this is the floor underneath it rather than a competitor to it.
//
// SetApplicationName is here rather than only on the Redis path for the same reason: without a
// shared, fixed name the purpose strings are derived from the content root, and a payload protected
// under one derived name cannot be unprotected under another.
builder.Services.AddDataProtection()
    .SetApplicationName("KiriChess")
    .PersistKeysToDbContext<KiriChessDbContext>();

// --- Identity ---
// Password policy (Phase 9d.1, restated 8.8a): at least 8 characters, containing an uppercase
// letter, a lowercase letter and a digit. RequireNonAlphanumeric is off - Google's password
// suggestions almost never include a symbol, and the client copy in login/reset-password restates
// this. The server is still the authority and rejects anything the client let through.
builder.Services
    .AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<KiriChessDbContext>()
    .AddDefaultTokenProviders();

// --- JWT ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<ApiTokenService>();

// --- Two-factor authentication (Phase 9d.2) ---
// Configure<T> rather than an eager read, same rule as RetentionOptions: the endpoints resolve
// IOptions<TwoFactorOptions> per request, so a WebApplicationFactory override is picked up.
// The challenge service is scoped - it takes a DbContext and is only ever called from a handler.
builder.Services.Configure<TwoFactorOptions>(builder.Configuration.GetSection(TwoFactorOptions.SectionName));
builder.Services.AddScoped<TwoFactorChallengeService>();

// --- Breached-password check (Phase 9d.1) ---
// DI registration itself is unconditional, unlike Email/ExternalAuth's conditional block above -
// only the Enabled flag (read inside BreachedPasswordChecker.IsBreachedAsync, off by default) decides
// whether the outbound call happens, so /register and /reset-password never need a null check. A
// short client timeout is what makes "fail open" actually fast rather than a registration that hangs
// for the default 100s before giving up.
var breachCheckOptions = BreachedPasswordCheckOptions.From(builder.Configuration);
builder.Services.AddSingleton(breachCheckOptions);
builder.Services.AddHttpClient(BreachedPasswordChecker.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
    c.Timeout = TimeSpan.FromSeconds(breachCheckOptions.TimeoutSeconds);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("KiriChess-BreachCheck/1.0");
});
builder.Services.AddSingleton<BreachedPasswordChecker>();

// --- Turnstile bot check (DH.3) ---
// Same fail-open shape as BreachedPasswordChecker just above: unconditional DI registration, the
// secret key's presence (read inside TurnstileVerifier.IsValidAsync) decides whether the outbound
// call happens, so /register never needs a null check. An unconfigured secret (every local/dev/test
// run today) and a Cloudflare outage both pass through; a missing or rejected token does not.
var turnstileOptions = TurnstileOptions.From(builder.Configuration);
builder.Services.AddSingleton(turnstileOptions);
builder.Services.AddHttpClient(TurnstileVerifier.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://challenges.cloudflare.com/");
    c.Timeout = TimeSpan.FromSeconds(turnstileOptions.TimeoutSeconds);
});
builder.Services.AddSingleton<TurnstileVerifier>();

// --- Transactional email (Phase 8a.1) ---
// Fails closed like Sentry:Dsn (6d.2) and Google sign-in (6b.3): with no provider configured the
// HTTP client and the dispatcher are never registered and email is logged instead of sent, which is
// also the only sender local development ever uses. Read off builder.Configuration here rather than
// after Build() because the enabled/disabled answer decides what gets registered - same as
// ExternalAuthOptions below.
var emailOptions = EmailOptions.From(builder.Configuration);
builder.Services.AddSingleton(emailOptions);

if (emailOptions.Enabled)
{
    builder.Services.AddHttpClient(ResendEmailSender.HttpClientName, c =>
    {
        c.BaseAddress = new Uri("https://api.resend.com/");
        c.Timeout = TimeSpan.FromSeconds(emailOptions.TimeoutSeconds);
    });
    builder.Services.AddSingleton<ResendEmailSender>();
    builder.Services.AddSingleton<QueuedEmailSender>();
    builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<QueuedEmailSender>());
    builder.Services.AddHostedService<EmailDispatchService>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
}

// --- Web push (11b.1 follow-up) ---
// Fails closed exactly like email above: with no VAPID key pair configured, IPushSender is never
// registered and PushNotificationService's own Enabled check short-circuits before anything would try
// to resolve it, so a deploy that never sets Vapid:* pays zero egress and zero startup cost.
var vapidOptions = VapidOptions.From(builder.Configuration);
builder.Services.AddSingleton(vapidOptions);
if (vapidOptions.Enabled)
{
    builder.Services.AddSingleton<IPushSender, WebPushSender>();
}
builder.Services.AddScoped<PushSubscriptionService>();
builder.Services.AddSingleton<PushNotificationService>();

// --- External auth / OAuth (Phase 6b.3) ---
// Fails closed: with no client id/secret (or no frontend base url) the provider handler is never
// registered, so /api/auth/external/google/start 404s instead of half-working. Same pattern as
// Sentry:Dsn (6d.2) and Metrics:ScrapeToken (6d.3).
var externalAuth = ExternalAuthOptions.From(builder.Configuration);
builder.Services.AddSingleton(externalAuth);
builder.Services.AddScoped<ExternalLoginService>();
builder.Services.AddScoped<ExternalLoginCodeService>();

var authBuilder = builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenDefaults.Scheme, _ => { })
    .AddPolicyScheme(HubAuthDefaults.Scheme, HubAuthDefaults.Scheme, o =>
    {
        o.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string bearer = "Bearer ";
            if (header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            {
                var value = header[bearer.Length..].Trim();
                if (value.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal))
                    return ApiTokenDefaults.Scheme;
            }

            var query = context.Request.Query["access_token"].ToString();
            if (query.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal))
                return ApiTokenDefaults.Scheme;

            return JwtBearerDefaults.AuthenticationScheme;
        };
    });

if (externalAuth.GoogleEnabled)
{
    // The OAuth `state`/correlation cookie (and the short-lived external-scheme cookie below) are
    // protected by ASP.NET Data Protection, whose key ring is ephemeral in this container, nothing
    // persists it. Consequences: a restart mid-handshake fails that one login attempt (the user
    // retries and it works), and the flow will not survive going multi-instance without a shared key
    // ring. That becomes a real blocker at roadmap 8.4 (Redis / multi-instance), alongside the
    // SignalR backplane. Do not configure a persisted key ring here, there is no Redis and no shared
    // volume yet, because DH.1/DH.2 are still open. Acceptable now because Phase 6's whole model is a
    // single pinned instance.
    authBuilder.AddCookie(ExternalAuthDefaults.ExternalScheme, o =>
    {
        o.Cookie.Name = "kirichess.external";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(5);   // it only has to survive one redirect hop
        o.SlidingExpiration = false;
    });

    authBuilder.AddGoogle(o =>
    {
        o.ClientId = externalAuth.GoogleClientId;
        o.ClientSecret = externalAuth.GoogleClientSecret;
        o.SignInScheme = ExternalAuthDefaults.ExternalScheme;
        o.CallbackPath = "/signin-google";
        // We never call Google's APIs on the user's behalf, so keeping its access/refresh tokens
        // would be pure liability with no use.
        o.SaveTokens = false;
        o.ClaimActions.MapJsonKey("email_verified", "email_verified", "boolean");

        // SameAsRequest, not Always: dev runs on plain http://localhost, where an Always-secure
        // correlation cookie is silently dropped and every sign-in fails "Correlation failed". In
        // production UseForwardedHeaders has already made Request.IsHttps true behind Fly/Cloudflare,
        // so this resolves to Secure there anyway.
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}

// Configured through IOptions rather than inline so the validation parameters are built from the
// *final* configuration, env vars and secrets are layered in after the builder runs, and reading
// the key eagerly here silently validates tokens against a stale key.
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            // Phase 8.5: the outbound JWT claim is ClaimTypes.Role (see TokenService), so the two ends
            // must agree explicitly on the inbound side too rather than by relying on the default map.
            RoleClaimType = ClaimTypes.Role
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"].ToString();
                var path = context.HttpContext.Request.Path;
                // A kirichess_pat_ token is not a JWT. Leaving it on context.Token makes JwtBearer fail
                // the hub negotiate before HubAuth can select the API-token scheme (Phase 9f.5).
                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments("/hubs")
                    && !accessToken.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

// Phase 8.5: the one role this codebase has. See ModerationPolicies for why a real role rather than
// the shared token 8.3's /analytics dashboard uses - a UserSanction row needs to attribute its action
// to a person.
builder.Services.AddAuthorization(o =>
{
    // Phase 9d.2: the role alone is no longer enough. An admin who has not enrolled can still sign in
    // and reach /profile to enrol - they simply cannot use the moderation surface until they have.
    // That soft-lock is what makes it impossible to lock the only moderator out of the only admin
    // account; see Phase 9d.2 scope decision 1.
    o.AddPolicy(ModerationPolicies.Admin, p => p
        .RequireRole(ModerationRoles.Admin)
        .RequireClaim(TwoFactorClaims.ClaimType, TwoFactorClaims.Enabled));

    // Phase 9f.5: named schemes so a read token cannot satisfy unversioned RequireAuthorization().
    o.AddPolicy(ApiTokenPolicies.Read, p => p
        .AddAuthenticationSchemes(ApiTokenDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.HasClaim(ApiTokenClaims.Scope, ApiTokenScopes.Read)
            || ctx.User.HasClaim(ApiTokenClaims.Scope, ApiTokenScopes.Play)));

    o.AddPolicy(ApiTokenPolicies.Play, p => p
        .AddAuthenticationSchemes(ApiTokenDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(ApiTokenClaims.Scope, ApiTokenScopes.Play));

    o.AddPolicy(ApiTokenPolicies.HubPlay, p => p
        .AddAuthenticationSchemes(HubAuthDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
        {
            var use = ctx.User.FindFirst(ApiTokenClaims.TokenUse)?.Value;
            if (use == ApiTokenClaims.Api)
                return ctx.User.HasClaim(ApiTokenClaims.Scope, ApiTokenScopes.Play);
            return true;
        }));
});

// --- Reverse proxy ---
// Fly terminates TLS and forwards plain HTTP. The proxy IPs are not knowable ahead of time, so the
// known-network allowlist is cleared. ForwardLimit stays at the framework default of 1: api is
// DNS-only, so there is one trusted hop (Fly), and raising it would let a caller of the public
// origin supply their own X-Forwarded-For. OriginHostLock then 404s non-probe traffic that arrives
// with Host kirichess-api.fly.dev (I-2-2), and 400s a Host outside OriginLock:AllowedHosts when
// that list is set (I-7-7). That is not "only reachable through a proxy"; it is the remaining lock
// that works on a grey API. Probe paths stay exempt so the 6PN metrics scrape is not refused.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// --- Multi-instance (Phase 8.4) ---
// Unlike metricsOptions/rateLimitOptions this MUST be read before Build(): the SignalR backplane
// and the Data Protection key ring are service registrations, and there is no post-Build() point
// at which they could be added. The cost is that a WebApplicationFactory test cannot toggle Redis
// on through ConfigureAppConfiguration alone, RedisMultiInstanceTests injects the options object
// directly instead, which is why AddKiriChessRedis takes a RedisOptions rather than IConfiguration.
var redisOptions = RedisOptions.From(builder.Configuration);
builder.Services.AddKiriChessRedis(redisOptions);

// --- Engine (stateless, safe as singleton) ---
builder.Services.AddSingleton<KiriChessEngine>();
builder.Services.AddSingleton<KiriChess.Server.Games.GameRegistry>();
builder.Services.AddScoped<KiriChess.Server.Ratings.RatingService>();
// --- Leaderboard sorted sets (Phase 8.4) ---
// Always registered, every method no-ops or returns null when Redis is disabled, so the endpoint
// keeps its Postgres path with no feature flag at the call site.
builder.Services.AddSingleton<LeaderboardCache>();
if (redisOptions.Enabled)
    builder.Services.AddHostedService<LeaderboardWarmup>();
// --- Glicko-2 between-period RD inflation ---
// Registered unconditionally, unlike the warm-up above: this is a Postgres-driven correction to the
// rating system itself, not a cache optimisation, so it must run on a deploy with no Redis at all.
builder.Services.AddScoped<KiriChess.Server.Ratings.RatingInactivitySweep>();
builder.Services.AddHostedService<KiriChess.Server.Ratings.RatingInactivitySweeper>();
builder.Services.AddScoped<KiriChess.Server.Games.RematchNotificationSweep>();
builder.Services.AddHostedService<KiriChess.Server.Games.RematchNotificationSweeper>();
builder.Services.AddSignalR(o =>
    {
        // 10a.2 first, and it is the only one of the three that acts on connect rather than on
        // invoke: a connection that is not admitted should not enter the rest of the chain at all.
        o.AddFilter<HubConnectionBudgetFilter>();
        o.AddFilter<HubObservabilityFilter>();
        o.AddFilter<HubRateLimitFilter>();   // must come after: observability stays the outer filter
        // S-4-1, innermost on purpose: the rate limiter above is a pure in-memory bucket, so a
        // revoked bot hammering the socket is thrown out by it before this one spends a database
        // read deciding whether the key is still alive.
        o.AddFilter<ApiTokenHubRevalidationFilter>();
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    .AddKiriChessBackplane(redisOptions);
// Phase 9d.8: the eligibility rules matchmaking and direct challenges must share. AddSingleton is
// correct - every collaborator it takes (GameRegistry, AbandonmentPolicy, SanctionPolicy,
// IRedisConnection, LiveGameIndex) is already a singleton itself.
builder.Services.AddSingleton<PlayEligibilityPolicy>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddSingleton<IGraceDisconnectStore, GraceDisconnectStore>();
builder.Services.AddSingleton<GameConnectionTracker>();
builder.Services.AddSingleton<SpectatorTracker>();
builder.Services.AddSingleton<PresenceTracker>();
// Phase 8.4 (Task C): refreshes PresenceTracker's/SpectatorTracker's Redis TTLs on the shared
// instance-heartbeat cadence rather than a timer of its own, see PresenceHeartbeat's doc comment.
builder.Services.AddSingleton<IInstanceHeartbeatHook, PresenceHeartbeat>();
// Registered twice on purpose: the hosted service and the hub must share one instance (`Current`).
builder.Services.AddSingleton<PresenceBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PresenceBroadcaster>());
builder.Services.AddHostedService<FlagFallMonitor>();
// --- Write-behind snapshot batching (Phase 9c.7) ---
// Bound rather than read here, like LagCompensationOptions below: configuration reads in this file
// are always deferred so WebApplicationFactory test overrides are picked up.
builder.Services.Configure<SnapshotPersistenceOptions>(
    builder.Configuration.GetSection(SnapshotPersistenceOptions.SectionName));
builder.Services.AddSingleton<LiveGamePersister>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveGamePersister>());
builder.Services.AddSingleton<GameRecoveryService>();

// --- Multi-instance game ownership (Phase 8.4) ---
// Always registered: each type checks IRedisConnection.Enabled and takes the single-instance path
// when Redis is absent, which keeps every call site free of feature flags.
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.GameOwnership>();
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.LiveGameIndex>();
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.GameDistribution>();
builder.Services.AddSingleton<GameActionExecutor>();
builder.Services.AddSingleton<IGameActionExecutor>(sp => sp.GetRequiredService<GameActionExecutor>());
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.GameCommandRouter>();
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.IGameCommandRouter>(sp => sp.GetRequiredService<KiriChess.Server.Games.Distributed.GameCommandRouter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<KiriChess.Server.Games.Distributed.GameCommandRouter>());  // one instance, two roles, cf. PresenceBroadcaster
// One instance, two hooks (cf. PresenceBroadcaster): it renews the leases on every heartbeat tick and
// releases them on a graceful stop. IInstanceShutdownHook runs inside InstanceHeartbeat.StopAsync,
// immediately before the deregistration, so "leases released before this instance is declared dead" is
// guaranteed by control flow rather than by hosted-service ordering (C-5-3).
builder.Services.AddSingleton<KiriChess.Server.Games.Distributed.GameOwnershipHeartbeat>();
builder.Services.AddSingleton<IInstanceHeartbeatHook>(sp => sp.GetRequiredService<KiriChess.Server.Games.Distributed.GameOwnershipHeartbeat>());
builder.Services.AddSingleton<IInstanceShutdownHook>(sp => sp.GetRequiredService<KiriChess.Server.Games.Distributed.GameOwnershipHeartbeat>());
if (redisOptions.Enabled)
    builder.Services.AddHostedService<KiriChess.Server.Games.Distributed.OrphanedGameSweeper>();

// --- Latency compensation (Phase 6f.2) ---
// Bound rather than read here: configuration reads in this file are always deferred, so
// WebApplicationFactory test overrides are picked up (see the comments around metricsOptions).
builder.Services.Configure<LagCompensationOptions>(
    builder.Configuration.GetSection(LagCompensationOptions.SectionName));
builder.Services.AddHostedService<LatencyProbe>();

// --- Abandonment/queue-abuse hardening (Phase 6e.3) ---
builder.Services.AddSingleton<AbandonmentPolicy>();
builder.Services.AddSingleton<MatchNoShowWatcher>();

// --- Direct challenge / play-a-friend links (Phase 9d.8) ---
// Configure<T> rather than an eager From(configuration) read - same reasoning as RetentionOptions
// below: WebApplicationFactory-based tests inject configuration overrides during Build() itself, and
// only a deferred binding picks those up. ChallengeService is scoped - it takes a DbContext and is
// only ever called from a request handler.
builder.Services.Configure<ChallengeOptions>(builder.Configuration.GetSection(ChallengeOptions.SectionName));
builder.Services.AddScoped<ChallengeService>();

// --- Pairing notification (Phase 9e.5) ---
// Scoped like ChallengeService above: it takes UserManager<ApplicationUser> and is only ever called
// from a request handler (the challenge-accept endpoint).
builder.Services.AddScoped<PairingNotificationService>();

// --- Rematch offer/accept (Phase 11a.1) ---
// Scoped like ChallengeService above: it takes a DbContext directly and GameHub is instantiated per
// invocation, so a scoped service injected into it lives exactly as long as that one hub call.
builder.Services.AddScoped<KiriChess.Server.Games.RematchService>();
builder.Services.AddScoped<KiriChess.Server.Games.FinishedGameReader>();

// --- Mail preferences / unsubscribe / reach-back notifications (Phase 11b.1) ---
// UnsubscribeTokenService is stateless after construction (it derives one HMAC key from Jwt:Key once)
// and NotificationPreferenceService is scoped like every other DbContext-holding service above.
// RematchNotificationService is a singleton that opens its own scope per delayed check, the same shape
// as MatchNoShowWatcher: it is armed once per offer from GameHub, which is itself instantiated per
// invocation and cannot outlive the hub call the way a delayed background timer needs to.
builder.Services.AddSingleton<UnsubscribeTokenService>();
builder.Services.AddScoped<NotificationPreferenceService>();
builder.Services.AddSingleton<KiriChess.Server.Games.RematchNotificationService>();

// --- Moderation (Phase 8.5) ---
// SanctionPolicy is a singleton opening its own scope, same shape as AbandonmentPolicy above, so it
// can be injected into the singleton MatchmakingService. SanctionService is scoped: it is only ever
// called from a request handler.
builder.Services.AddSingleton<SanctionPolicy>();
builder.Services.AddScoped<SanctionService>();

// S-3-1: the ban-evasion marker written at erasure and checked at registration. Configure<T> rather
// than an eager From(configuration) read, same rule as RetentionOptions and TwoFactorOptions: the
// service resolves IOptions<BanEvasionOptions> per scope, so a WebApplicationFactory override is
// picked up. Registration is unconditional and the secret's presence decides whether anything
// happens (read inside BanEvasionMarker), so neither /register nor AccountDeletionService needs a
// null check - the same shape as TurnstileVerifier above. Scoped: it takes a DbContext.
builder.Services.Configure<BanEvasionOptions>(builder.Configuration.GetSection(BanEvasionOptions.SectionName));
builder.Services.AddScoped<BanEvasionMarker>();

// Phase 8a.4: display-name content policy. ContentPolicy is the parsed embedded blocklist and is
// immutable, so it is a singleton; DisplayNameService takes a DbContext and is scoped.
builder.Services.AddSingleton(ContentPolicy.Default);
builder.Services.AddScoped<DisplayNameService>();
// ContentModerationOptions is resolved from a factory rather than constructed eagerly here, so the
// actual configuration read happens the first time the container resolves it - after builder.Build()
// runs, same rule as RateLimitOptions.From inside AddKiriChessRateLimiting's callback (see
// RateLimiting.cs's header comment and the 6a.8 post-mortem it cites). This matters because
// WebApplicationFactory-based tests inject configuration overrides during Build() itself; a value
// captured off builder.Configuration before that would silently miss them.
builder.Services.AddSingleton(sp => ContentModerationOptions.From(sp.GetRequiredService<IConfiguration>()));

// Phase 8a.2: GDPR export and erasure. Both are scoped - each takes a DbContext and is only ever
// called from a request handler.
builder.Services.AddScoped<AccountExportBuilder>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<AvatarService>();

// Patron badge: PatronOptions is a plain config record (Options.From style), PatronService is
// scoped like every other DbContext-holding service above.
builder.Services.AddSingleton(PatronOptions.From(builder.Configuration));
builder.Services.AddScoped<PatronService>();

// IGameClock had no registration before the patron badge needed one: GameSession constructs its own.
// SystemGameClock is stateless, so one shared instance is correct.
builder.Services.AddSingleton<IGameClock>(KiriChess.Server.Games.SystemGameClock.Instance);

// --- Data retention (Phase 8a.3) ---
// Configure<T> rather than an eager Options.From(configuration) read: the sweeper is always
// registered and reads Enabled at tick time, so the deferred binding is what picks up
// WebApplicationFactory's configuration overrides. RetentionSweep is scoped (it takes a DbContext)
// and the hosted service opens a scope per sweep.
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddScoped<RetentionSweep>();
builder.Services.AddHostedService<RetentionSweeper>();

// --- Observability (Phase 6d) ---
builder.Services.AddSingleton<KiriChessMetrics>();
// Same double registration as PresenceBroadcaster: the observable gauges must be created exactly
// once, and the hosted-service registration is only there to force eager construction.
builder.Services.AddSingleton<GameMetricsObserver>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameMetricsObserver>());
builder.Services.AddSingleton<HubObservabilityFilter>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.AddProblemDetails();

var otelBuilder = builder.Services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter(KiriChessMetrics.MeterName)
    .AddAspNetCoreInstrumentation()   // http.server.request.duration, by route and status
    .AddRuntimeInstrumentation()      // GC, thread pool, exceptions, "is the process healthy"
    .AddPrometheusExporter());

// --- Traces (Phase 9e.7) ---
// Same fails-closed gate as the Sentry block above, and for the same reason: no DSN means no
// exporter and zero egress. AddAspNetCoreInstrumentation is what turns HttpContext.TraceIdentifier
// (already surfaced in every ProblemDetails response by UnhandledExceptionHandler, 6d.2) from a
// per-connection counter into a real W3C trace id, which is the thing that makes that id resolve to
// something instead of nothing. AddSentryOtlpExporter derives the OTLP endpoint and auth headers
// from the DSN itself, so there is no separate Otlp:Endpoint to configure.
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    otelBuilder.WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddSentryOtlpExporter(sentryDsn));
}

// --- Rate limiting (Phase 6e.1) ---
// AddRateLimiter's policy callback is deferred until RateLimiterOptions is first resolved by the
// framework (after Build()), so reading configuration inside it is safe, see the comment inside
// AddKiriChessRateLimiting. HubRateLimitFilter is always added to the hub filter chain: SignalR is
// configured here, before Build() ever runs, so there is no later point at which this file could
// conditionally omit it the way the HTTP middleware below is conditionally omitted; the filter reads
// RateLimiting:Enabled itself and simply forwards every call when it is false.
builder.Services.AddKiriChessRateLimiting(builder.Configuration);
builder.Services.AddSingleton<HubRateLimitFilter>();

// --- API-token re-validation on live hub connections (S-4-1) ---
// A singleton for the same reason HubRateLimitFilter is one: it holds per-connection state that must
// survive between invocations, and the chain is fixed here, before Build(). It opens its own scope
// per re-check rather than taking a scoped ApiTokenService, since a singleton cannot hold one.
builder.Services.AddSingleton<ApiTokenHubRevalidationFilter>();

// --- Hub connection budget (Phase 10a.2) ---
// A counter of live connections beside the existing counter of invocations. Registered
// unconditionally for the same reason HubRateLimitFilter is: SignalR's filter chain is fixed before
// Build(), so the Enabled gate lives inside the budget rather than around this registration. The
// budget itself is the singleton, because it is the state; the filter is a thin application of it.
builder.Services.AddSingleton<HubConnectionBudget>();
builder.Services.AddSingleton<HubConnectionBudgetFilter>();

// --- Kill switches (Phase 10c.4) ---
// A singleton, like LiveGamePersister above: a scoped DbContext for a yes/no question that changes
// rarely would put a database round trip on every register/JoinQueue/JoinAsSpectator/GET-seeks call.
builder.Services.AddSingleton<KillSwitchService>();

var app = builder.Build();

// Read from app.Configuration (the final, fully-composed configuration), not builder.Configuration:
// WebApplicationFactory-based tests inject their configuration overrides as part of Build() itself,
// so a read against builder.Configuration here would silently miss them (every other configuration
// read in this file is deferred, IOptions/lazy lambdas, and only sees the final config for the
// same reason; this is the one eager read, so it has to be the one that waits for Build()).
var metricsOptions = MetricsOptions.From(app.Configuration);
// --- Rate limiting (Phase 6e.1) ---
// Same reasoning as metricsOptions above: read after Build() so the Enabled gate used below
// (UseKiriChessRateLimiter) sees the fully composed configuration, including test overrides.
var rateLimitOptions = RateLimitOptions.From(app.Configuration);
// --- Moderation (Phase 8.5) ---
// Same reasoning as metricsOptions above.
var moderationOptions = ModerationOptions.From(app.Configuration);

if (!externalAuth.GoogleEnabled)
{
    app.Logger.LogInformation(
        "External sign-in is disabled: set Authentication:Google:ClientId, Authentication:Google:ClientSecret "
        + "and Frontend:BaseUrl to enable Google sign-in.");
}

if (!emailOptions.Enabled)
    app.Logger.LogInformation(
        "Transactional email is not configured: messages are logged, not sent. Set Email:ApiKey, "
        + "Email:FromAddress and Frontend:BaseUrl to enable delivery.");

if (moderationOptions.AdminEmails.Count == 0)
    app.Logger.LogInformation(
        "No moderators are configured: set Moderation:AdminEmails to grant access to /api/moderation.");

if (!redisOptions.Enabled)
    app.Logger.LogInformation(
        "Redis is not configured: running single-instance (SignalR backplane, cross-instance game "
        + "ownership, shared presence and leaderboard cache are all inactive). Set Redis:Configuration to enable.");
else
    app.Logger.LogInformation("Redis multi-instance mode enabled; instance id {InstanceId}",
        app.Services.GetRequiredService<InstanceIdentity>().Id);

StartupValidation.ValidateJwt(app.Services);

// --- Migration-only mode ---
// `dotnet KiriChess.Server.dll --migrate` applies migrations and exits. Schema migration is a
// deliberate deploy step, not a boot-time race between instances.
if (args.Contains("--migrate"))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
    await migrationDb.Database.MigrateAsync();
    app.Logger.LogInformation("Migrations applied; exiting without serving.");
    return;
}

app.UseForwardedHeaders();
app.UseOriginHostLock();            // I-2-2 / I-7-7: before anything that would treat a foreign Host as the API
app.UseKiriChessSecurityHeaders();   // 9c.1: before everything, so even an error response carries it
app.UseExceptionHandler();          // must precede everything that can throw
app.UseMetricsScrapeGuard(metricsOptions);

// TLS is terminated at the edge in every deployed environment, so redirecting here would only
// bounce healthy internal traffic. Opt in locally via Server:EnableHttpsRedirection.
if (app.Configuration.GetValue("Server:EnableHttpsRedirection", false))
    app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);
app.UseAuthentication();
app.UseMiddleware<UserScopeMiddleware>();   // after UseAuthentication: before it, User is anonymous
app.UseMiddleware<HubAuthOverlayMiddleware>(); // /hubs/game + /api/v1: JWT stays the app default
// --- Rate limiting (Phase 6e.1) ---
// Must run after UseAuthentication(): the per-user partitions read HttpContext.User, and before
// UseAuthentication every request looks anonymous and would collapse into one shared IP partition.
app.UseKiriChessRateLimiter(rateLimitOptions);
// --- Hub connection budget (Phase 10a.2) ---
// After UseAuthentication() for the same reason as the limiter above: the reserved share is for
// authenticated callers, and before UseAuthentication every negotiate request looks anonymous and
// would be measured against the spectator ceiling. Only touches /hubs/*/negotiate; every other
// request passes straight through.
app.UseHubConnectionBudgetProbe();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapExternalAuthEndpoints();
app.MapGameEndpoints();
app.MapPublicApiEndpoints();   // 9f.5: versioned bot/public surface, API-token only
app.MapChallengeEndpoints();   // 9d.8: play-a-friend links
app.MapSeekEndpoints();   // 9e.4: public open-seek list
app.MapReplayEndpoints();   // 8.2: public, anonymous, finished games only
app.MapRatingEndpoints();
app.MapPublicProfileEndpoints();   // public profile: anonymous read of one account, like /leaderboards and /replay
app.MapReportEndpoints();
app.MapModerationEndpoints();
app.MapContentEndpoints();   // 8a.4: display-name check, rename, and the moderator scan
app.MapAccountEndpoints();   // 8a.2: GDPR data export and account erasure
app.MapNotificationEndpoints();   // 11b.1: anonymous one-click unsubscribe
app.MapPushEndpoints();   // 11b.1 follow-up: web push subscribe/unsubscribe
app.MapStripeWebhookEndpoints(); // Stripe Webhooks
app.MapDonationEndpoints();   // patron badge: the client_reference_id handshake and the own-status read
app.MapTwoFactorEndpoints();   // 9d.2: TOTP enrolment and management
app.MapRetentionEndpoints();   // 8a.3: the dormant-account report. Read-only - nothing here erases anything.
app.MapKillSwitchEndpoints();   // 10c.4: the four things an incident can turn off without a deploy
// Phase 8.5 detectors. Both are advisory and admin-only: they compute a score and the evidence
// behind it, and nothing in this codebase reads that score and acts on it.
app.MapSandbaggingEndpoints();
app.MapPairConcentrationEndpoints();
app.MapEngineDetectionEndpoints();
app.MapAnalyticsEndpoints();
app.MapCounterEndpoints();   // AD.6/11a.3: the anonymous counter beacon and the admin traffic read
app.MapHub<GameHub>("/hubs/game");
app.MapHub<PresenceHub>("/hubs/presence");

// Phase 9f.5: the published OpenAPI document is the `public` one (api/v1 only). Never map the
// first-party document - MapOpenApi() would expose /openapi/v1.json as the full admin surface.
//
// S-1-5: this path is anonymous and deliberately exempt from the rate limiter (a scraper must never
// be throttled), which used to mean every request re-walked the whole route table and re-ran four
// transformers to rebuild 51KB of JSON from scratch - an unauthenticated amplification lever on the
// one process that also runs every live game's clock. The document cannot change without a redeploy,
// so build it once per process and serve the cached string instead. Lazy, not eager: the ApiExplorer
// data the generator reads is only realized once the host has actually started (see
// OpenApiExport.WriteAsync's own comment on this), and building it here at route-mapping time would
// run before app.Run() and silently capture an empty document. The first real HTTP request always
// arrives after start, so evaluating Value there is safe.
var cachedPublicOpenApiDocument = new Lazy<Task<string>>(() => OpenApiExport.SerializeAsync(app.Services, "public"));
app.MapGet("/openapi/v1.json", async () =>
{
    var json = await cachedPublicOpenApiDocument.Value;
    return Results.Text(json, "application/json;charset=utf-8");
}).AllowAnonymous();

if (metricsOptions.Enabled)
    app.MapPrometheusScrapingEndpoint(MetricsOptions.Path);

// --- OpenAPI export mode (Phase 9d.7), continued ---
// Placement is load-bearing and worth a comment: it must be after every route above is mapped (the
// document is built from the route table) and before the boot-time migration, the moderation admin
// seed and GameRecoveryService.RecoverAsync() below, all three of which want a database this mode
// does not have.
if (exportOpenApiPath is not null)
{
    // StartAsync, not just Build(): confirmed empirically that the ApiExplorer data AddOpenApi's
    // document generator reads from is only realized once the host has actually started - a
    // WebApplication that is only Build()-built and never started reports zero endpoints and the
    // document comes back with an empty `paths` object, silently. DeterministicConfiguration's
    // Urls=http://127.0.0.1:0 is what keeps this from fighting a real dev server for a fixed port.
    await app.StartAsync();
    await OpenApiExport.WriteAsync(app, exportOpenApiPath);
    app.Logger.LogInformation("OpenAPI document written to {Path}; exiting without serving.", exportOpenApiPath);
    await app.StopAsync();
    return;
}

// Migrating on boot is a race as soon as there is more than one instance; keep it for local dev
// convenience only. Production applies migrations via `--migrate` as a deploy step.
var migrateOnStartup = app.Configuration.GetValue("RunMigrationsOnStartup", app.Environment.IsDevelopment());
if (migrateOnStartup || (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("SeedDemoData")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

    if (migrateOnStartup)
        await db.Database.MigrateAsync();

    if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("SeedDemoData"))
        await DevSeeder.SeedAsync(db);
}

// --- Moderation admin seeding (Phase 8.5) ---
// Its own block rather than folded into the one above: it must run on every boot that has admins
// configured, not only when migrateOnStartup or SeedDemoData is set. Wrapped in try/catch so a
// database that is not reachable at boot does not stop the server from starting - it already
// tolerates that today for recovery, and a moderation seed failure is retried on the next boot.
if (moderationOptions.AdminEmails.Count > 0)
{
    try
    {
        using var moderationScope = app.Services.CreateScope();
        var roleManager = moderationScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = moderationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await ModerationSeeder.SeedAsync(roleManager, userManager, moderationOptions, app.Logger);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to seed moderation admins at boot; will retry on next boot.");
    }
}

// Rebuild live games from their persisted move logs before serving the first request, a deploy
// or crash must never silently eat an in-progress game (Phase 6c).
await app.Services.GetRequiredService<GameRecoveryService>().RecoverAsync();

app.Run();

// --- OpenAPI export mode (Phase 9d.7), helper ---
// The value following a flag, e.g. ReadArgValue(args, "--export-openapi") for
// `--export-openapi docs/api/openapi.json` returns "docs/api/openapi.json"; null when the flag is
// absent or is the last argument with nothing after it.
static string? ReadArgValue(string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void ConfigureKiriChessOpenApi(Microsoft.AspNetCore.OpenApi.OpenApiOptions options, bool publicDocument)
{
    options.ShouldInclude = description =>
    {
        var path = description.RelativePath;
        if (path is null) return false;
        if (publicDocument)
            return path.StartsWith("api/v1/", StringComparison.Ordinal);
        return path.StartsWith("api/", StringComparison.Ordinal)
            && !path.StartsWith("api/v1/", StringComparison.Ordinal);
    };
    options.AddSchemaTransformer<StringEnumSchemaTransformer>();
    options.AddSchemaTransformer<NumericSchemaTransformer>();
    options.AddOperationTransformer<MultiShapeResponseTransformer>();
    options.AddDocumentTransformer(new HubPayloadDocumentTransformer(publicDocument));
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = publicDocument ? "KiriChess Public API" : "KiriChess API";
        document.Info.Version = "1.0.0";
        document.Info.Description = publicDocument
            ? "The published bot/HTTP API (Phase 9f.5). Authenticate with a personal access token "
              + "issued at POST /api/account/api-tokens (first-party, JWT only). Raw tokens start with "
              + "`kirichess_pat_` and carry `read` and/or `play`. The 43-character secret is followed "
              + "by a 6-character CRC-32 checksum. API-created games and challenges are always "
              + "unrated; rated play is hub JoinQueue remapped onto bot:{preset}. Live clocks stay on "
              + "/hubs/game. Those payloads are not HTTP, so they live in components.schemas referenced "
              + "by no path, each marked with the x-kirichess-transport extension naming the hub and "
              + "the event or method; /hubs/presence is omitted because an API token cannot "
              + "authenticate there. How to call it is documented at https://kirichess.com/developers."
            : "The KiriChess HTTP API. This document is generated (Phase 9d.7) - see docs/api/openapi.json, "
              + "generated by `dotnet run --project src/KiriChess.Server -- --export-openapi`, and "
              + "ui/lib/api.generated.ts, generated from that file by `npm run gen:api` in ui/. The SignalR "
              + "hubs at /hubs/game and /hubs/presence are real-time, not HTTP, so their payload schemas "
              + "(Phase 9e.9) live in components.schemas referenced by no path, each marked with the "
              + "x-kirichess-transport extension naming the hub and the event or method it belongs to. "
              + "The published third-party surface is /api/v1 and lives in a separate document.";

        // M-6-5. The first-party document is an internal contract and stays without a server URL.
        // The public one is what a stranger imports into Postman or a generator, and an empty
        // servers list leaves that client with no base URL.
        document.Servers = publicDocument
            ? [new Microsoft.OpenApi.OpenApiServer { Url = "https://api.kirichess.com" }]
            : [];

        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearerAuth"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = publicDocument ? "API token (kirichess_pat_...)" : "JWT",
            Description = publicDocument
                ? "A personal access token issued once at POST /api/account/api-tokens. Prefix kirichess_pat_, then 43 secret characters and a 6-character CRC-32 checksum. Scopes: read, play."
                : null
        };

        // M-6-5. Every /api/v1 operation requires a kirichess_pat_ token, so the requirement is document-level
        // rather than repeated on each path. The first-party document mixes anonymous, cookie and
        // JWT routes, and a global requirement there would be a lie about those routes.
        if (publicDocument)
        {
            document.Security =
            [
                new Microsoft.OpenApi.OpenApiSecurityRequirement
                {
                    [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("bearerAuth", document)] = []
                }
            ];
        }

        return Task.CompletedTask;
    });
}

public partial class Program { }
