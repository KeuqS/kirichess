using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// $2-2: the badge rule is inlined at six call sites that project anonymous types. This puts one
/// active one-off, one lapsed one-off, one monthly (with a lapsed banked expiry) and one never-patron
/// account through each copy and asserts the tier string equals <see cref="PatronBadge.For"/>.
/// The inlining stays; only the test reaches it. Requires Docker.
/// </summary>
public sealed class PatronBadgeSurfaceTests : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    private readonly RedisFixture _redis;
    private PostgreSqlContainer? _pg;

    public PatronBadgeSurfaceTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Every_inlined_copy_agrees_with_PatronBadge_For()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var connectionString = _pg!.GetConnectionString();
        await using var factory = BuildFactory(connectionString, redisConfiguration: null);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        using var client = factory.CreateClient();
        var active = await RegisterAsync(client, "badge-active@example.com", "Badge Active");
        var lapsed = await RegisterAsync(client, "badge-lapsed@example.com", "Badge Lapsed");
        var monthly = await RegisterAsync(client, "badge-monthly@example.com", "Badge Monthly");
        var never = await RegisterAsync(client, "badge-never@example.com", "Badge Never");

        var now = factory.Services.GetRequiredService<IGameClock>().UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await StampPatronAsync(db, active.UserId, PatronTier.Rook, PatronKind.OneTime, now.AddMonths(6));
            await StampPatronAsync(db, lapsed.UserId, PatronTier.Queen, PatronKind.OneTime, now.AddDays(-2));
            await StampPatronAsync(db, monthly.UserId, PatronTier.Bishop, PatronKind.Monthly, now.AddDays(-2));
            AddRatings(db, active.UserId, 1800);
            AddRatings(db, lapsed.UserId, 1700);
            AddRatings(db, monthly.UserId, 1600);
            AddRatings(db, never.UserId, 1500);
            SeedFinishedGame(db, active.UserId, lapsed.UserId);
            SeedFinishedGame(db, monthly.UserId, never.UserId);
            await db.SaveChangesAsync();
        }

        var expected = await ExpectedTiersAsync(factory, now);
        var users = new[] { active, lapsed, monthly, never };

        var namesActiveLapsed = await GameNameResolver.ResolveNamesAsync(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            active.UserId, lapsed.UserId);
        Assert.Equal(expected[active.UserId], namesActiveLapsed.WhitePatronTier);
        Assert.Equal(expected[lapsed.UserId], namesActiveLapsed.BlackPatronTier);

        var namesMonthlyNever = await GameNameResolver.ResolveNamesAsync(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            monthly.UserId, never.UserId);
        Assert.Equal(expected[monthly.UserId], namesMonthlyNever.WhitePatronTier);
        Assert.Equal(expected[never.UserId], namesMonthlyNever.BlackPatronTier);

        Guid gameActiveLapsed;
        Guid gameMonthlyNever;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            gameActiveLapsed = await db.Games.Where(g => g.WhitePlayerId == active.UserId).Select(g => g.Id).SingleAsync();
            gameMonthlyNever = await db.Games.Where(g => g.WhitePlayerId == monthly.UserId).Select(g => g.Id).SingleAsync();
        }

        await AssertReplayAsync(client, $"/api/games/{gameActiveLapsed}/replay", expected[active.UserId], expected[lapsed.UserId]);
        await AssertReplayAsync(client, $"/api/games/{gameMonthlyNever}/replay", expected[monthly.UserId], expected[never.UserId]);

        await AssertLeaderboardAsync(client, "/api/leaderboards/steal1", users, expected);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", never.Token);
        var createdToken = await client.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = "badge-reader", Scopes = new[] { "read" }, Password }, JsonOptions);
        createdToken.EnsureSuccessStatusCode();
        var token = await createdToken.Content.ReadFromJsonAsync<CreatedApiTokenDto>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(token?.Token));

        using var apiClient = factory.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);

        await AssertLeaderboardAsync(apiClient, "/api/v1/leaderboards/bot/steal1", users, expected);
        await AssertReplayAsync(apiClient, $"/api/v1/games/{gameActiveLapsed}/replay", expected[active.UserId], expected[lapsed.UserId]);
        await AssertReplayAsync(apiClient, $"/api/v1/games/{gameMonthlyNever}/replay", expected[monthly.UserId], expected[never.UserId]);

        Assert.True(_redis.Container is not null, "Redis fixture started with Docker; a null container here is a fixture bug.");
        await using var cachedFactory = BuildFactory(connectionString, _redis.Container!.GetConnectionString());
        Assert.True(
            cachedFactory.Services.GetRequiredService<IRedisConnection>().Enabled,
            "The cache-backed host has Redis disabled, so the cache copies of the badge rule would not run.");

        using (var scope = cachedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<LeaderboardCache>();
            var stealRows = await db.Ratings.Where(r => r.Pool == "steal1")
                .Select(r => new { r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt }).ToListAsync();
            var botPool = RatingConstants.ToBotPool("steal1");
            var botRows = await db.Ratings.Where(r => r.Pool == botPool)
                .Select(r => new { r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt }).ToListAsync();
            Assert.True(await cache.RebuildAsync("steal1",
                stealRows.Select(r => new LeaderboardCacheRow(r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt)).ToArray()));
            Assert.True(await cache.RebuildAsync(botPool,
                botRows.Select(r => new LeaderboardCacheRow(r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt)).ToArray()));
        }

        using var cachedClient = cachedFactory.CreateClient();
        await AssertLeaderboardAsync(cachedClient, "/api/leaderboards/steal1", users, expected);

        using var cachedApiClient = cachedFactory.CreateClient();
        cachedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        await AssertLeaderboardAsync(cachedApiClient, "/api/v1/leaderboards/bot/steal1", users, expected);
    }

    private static async Task<Dictionary<Guid, string>> ExpectedTiersAsync(WebApplicationFactory<Program> factory, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var rows = await db.Users.AsNoTracking().ToListAsync();
        return rows.ToDictionary(u => u.Id, u => PatronBadge.For(u, now).ToString());
    }

    private static async Task StampPatronAsync(
        KiriChessDbContext db, Guid userId, PatronTier tier, PatronKind kind, DateTimeOffset expiresAt)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsPatron = true;
        user.PatronTier = tier;
        user.PatronKind = kind;
        user.PatronExpiresAt = expiresAt;
        user.PatronSince = expiresAt.AddMonths(-1);
        if (kind == PatronKind.Monthly)
            user.StripeSubscriptionId = "sub_badge_surface";
    }

    private static void AddRatings(KiriChessDbContext db, Guid userId, double value)
    {
        db.Ratings.Add(new Rating
        {
            UserId = userId,
            Pool = "steal1",
            Value = value,
            RatingDeviation = 60,
            Volatility = 0.06,
            GamesPlayed = 20
        });
        db.Ratings.Add(new Rating
        {
            UserId = userId,
            Pool = RatingConstants.ToBotPool("steal1"),
            Value = value,
            RatingDeviation = 60,
            Volatility = 0.06,
            GamesPlayed = 20
        });
    }

    private static void SeedFinishedGame(KiriChessDbContext db, Guid whiteId, Guid blackId)
    {
        db.Games.Add(new Game
        {
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = GameResult.WhiteWins,
            Termination = TerminationReason.Checkmate,
            RatingPool = "steal1",
            IsRated = true,
            StartFen = KiriChessEngine.StartFen,
            ConfigSnapshot = KiriChessConfig.Presets["steal1"].Clone(),
            Moves = [],
            PlyCount = 0,
            EndedAt = DateTimeOffset.UtcNow
        });
    }

    private static async Task AssertReplayAsync(HttpClient client, string path, string whiteTier, string blackTier)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ReplayPatronDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(whiteTier, dto!.WhitePatronTier);
        Assert.Equal(blackTier, dto.BlackPatronTier);
    }

    private static async Task AssertLeaderboardAsync(
        HttpClient client, string path, AuthResult[] users, Dictionary<Guid, string> expected)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<LeaderboardPatronDto[]>(JsonOptions);
        Assert.NotNull(rows);
        foreach (var user in users)
        {
            var row = Assert.Single(rows!, r => r.UserId == user.UserId);
            Assert.Equal(expected[user.UserId], row.PatronTier);
        }
    }

    private static async Task<AuthResult> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password,
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    private static WebApplicationFactory<Program> BuildFactory(string postgresConnectionString, string? redisConfiguration)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = postgresConnectionString,
                    ["Jwt:Key"] = "patron-badge-surface-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Auth:BreachedPasswordCheckEnabled"] = "false",
                    ["RunMigrationsOnStartup"] = "false"
                };
                if (redisConfiguration is not null)
                {
                    settings["Redis:Configuration"] = redisConfiguration;
                    settings["Redis:KeyPrefix"] = $"test-{Guid.NewGuid():N}:";
                }
                cfg.AddInMemoryCollection(settings);
            });

            if (redisConfiguration is not null)
                builder.ConfigureTestServices(services =>
                {
                    var identity = new InstanceIdentity($"test-{Guid.NewGuid():N}");
                    var options = new RedisOptions(redisConfiguration, $"test-{Guid.NewGuid():N}:", 30, 10, 5);
                    services.AddSingleton(identity);
                    services.AddSingleton(options);
                    services.AddSingleton<IRedisConnection>(new RedisConnection(options, identity));
                });
        });

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record CreatedApiTokenDto(Guid Id, string Token);

    private sealed record ReplayPatronDto(string WhitePatronTier, string BlackPatronTier);

    private sealed record LeaderboardPatronDto(Guid UserId, string PatronTier);
}
