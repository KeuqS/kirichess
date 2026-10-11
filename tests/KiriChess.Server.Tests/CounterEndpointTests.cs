using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Analytics;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end proof of the AD.6 / 11a.3 counter beacon and traffic read: the migration actually
/// applies, the raw <c>ON CONFLICT</c> SQL in <c>DailyCounterRecorder</c> hits the real table and
/// column names, the write route needs no auth, and the read route is admin-only and leaks no
/// identifier. Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class CounterEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "counter-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Known_key_increments_atomically()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var r1 = await PostCounterAsync("landing");
        var r2 = await PostCounterAsync("landing");
        Assert.Equal(HttpStatusCode.NoContent, r1.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);

        var row = await SingleRowAsync();
        Assert.Equal(2, row.Count);
        Assert.Equal(CounterKeys.Landing, row.CounterKey);
        Assert.Equal(CounterSources.Direct, row.Source);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), row.Date);
    }

    [SkippableFact]
    public async Task Unknown_key_is_204_and_writes_nothing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await PostCounterAsync("not-a-real-counter-key");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await RowCountAsync());
    }

    [SkippableFact]
    public async Task Null_or_empty_key_is_204_and_writes_nothing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var nullKeyResponse = await _client!.PostAsJsonAsync(
            "/api/analytics/counter", new CounterRequestBody(null, null), JsonOptions);
        var emptyKeyResponse = await PostCounterAsync("");

        Assert.Equal(HttpStatusCode.NoContent, nullKeyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, emptyKeyResponse.StatusCode);
        Assert.Equal(0, await RowCountAsync());
    }

    [SkippableFact]
    public async Task Case_and_whitespace_normalise_before_the_allowlist_check()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await PostCounterAsync("  LANDING  ");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await SingleRowAsync();
        Assert.Equal(CounterKeys.Landing, row.CounterKey);
    }

    [SkippableFact]
    public async Task Allowlisted_source_gets_its_own_row()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await PostCounterAsync("landing", "reddit");
        await PostCounterAsync("landing");

        var rows = await AllRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Source == "reddit" && r.Count == 1);
        Assert.Contains(rows, r => r.Source == CounterSources.Direct && r.Count == 1);
    }

    [SkippableFact]
    public async Task Unknown_source_collapses_to_direct_and_is_still_counted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await PostCounterAsync("landing", "not-a-channel");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await SingleRowAsync();
        Assert.Equal(CounterSources.Direct, row.Source);
        Assert.DoesNotContain(await AllRowsAsync(), r => r.Source == "not-a-channel");
    }

    [SkippableFact]
    public async Task Funnel_key_is_accepted_like_any_other_key()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await PostCounterAsync("queue-join");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await SingleRowAsync();
        Assert.Equal(CounterKeys.QueueJoin, row.CounterKey);
    }

    [SkippableFact]
    public async Task First_game_empty_lobby_and_rematch_keys_are_accepted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        string[] keys =
        [
            CounterKeys.FirstGameFinish,
            CounterKeys.EmptyLobbyLeave,
            CounterKeys.RematchOffer,
            CounterKeys.RematchAccept,
        ];

        foreach (var key in keys)
        {
            var response = await PostCounterAsync(key);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var rows = await AllRowsAsync();
        Assert.Equal(4, rows.Count);
        foreach (var key in keys)
        {
            var row = Assert.Single(rows, r => r.CounterKey == key);
            Assert.Equal(1, row.Count);
            Assert.Equal(CounterSources.Direct, row.Source);
        }
    }

    [SkippableFact]
    public async Task Beacon_needs_no_auth()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // No Authorization header at all - _client is never given one in this test. Deliberate, not
        // an oversight: this route is fired by every anonymous visitor.
        Assert.Null(_client!.DefaultRequestHeaders.Authorization);

        var response = await PostCounterAsync("landing");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [SkippableFact]
    public async Task Traffic_route_is_admin_only()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await PostCounterAsync("landing");
        await PostCounterAsync("landing");
        await RegisterAsync("traffic-admin-only-signup@example.com", "SignupOnly");

        var anonymous = await _client!.GetAsync("/api/analytics/traffic");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var nonAdmin = await RegisterAsync("traffic-non-admin@example.com", "Non Admin");
        AuthorizeAs(nonAdmin);
        var forbidden = await _client!.GetAsync("/api/analytics/traffic");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var admin = await RegisterAndPromoteAsync("traffic-admin@example.com", "Admin One");
        AuthorizeAs(admin);
        var ok = await _client!.GetAsync("/api/analytics/traffic");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var body = await ok.Content.ReadFromJsonAsync<TrafficSummaryDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(2, body!.TotalPageViews);
        Assert.True(body.TotalSignups >= 1);
    }

    [SkippableFact]
    public async Task Traffic_response_carries_no_identifiers()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await PostCounterAsync("landing");
        var signup = await RegisterAsync("no-identifiers-signup@example.com", "NoIdent");
        var admin = await RegisterAndPromoteAsync("no-identifiers-admin@example.com", "AdminNoIdent");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/analytics/traffic");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("@", raw);
        Assert.DoesNotContain(signup.UserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(admin.UserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Traffic_game_counts_omit_bot_pools_and_keep_custom_games()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("traffic-bot-admin@example.com", "Traffic Bot Admin");
        var white = await RegisterAsync("traffic-bot-white@example.com", "Traffic White");
        var black = await RegisterAsync("traffic-bot-black@example.com", "Traffic Black");
        var now = DateTimeOffset.UtcNow;

        await SeedFinishedGameAsync(white.UserId, black.UserId, "steal1", now.AddMinutes(-10));
        await SeedFinishedGameAsync(white.UserId, black.UserId, "bot:steal1", now.AddMinutes(-8));
        await SeedFinishedGameAsync(white.UserId, black.UserId, pool: null, now.AddMinutes(-6));

        AuthorizeAs(admin);
        var summary = await _client!.GetFromJsonAsync<TrafficSummaryDto>("/api/analytics/traffic", JsonOptions);

        Assert.Equal(2, summary!.FirstSession.GamesStarted);
        Assert.Equal(2, summary.FirstSession.GamesFinished);
    }

    private async Task SeedFinishedGameAsync(Guid whiteId, Guid blackId, string? pool, DateTimeOffset endedAt)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(new Game
        {
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = GameResult.WhiteWins,
            Termination = TerminationReason.Resignation,
            RatingPool = pool,
            IsRated = pool is not null,
            StartFen = string.Empty,
            ConfigSnapshot = new KiriChessConfig(),
            Moves = [],
            PlyCount = 6,
            CreatedAt = endedAt.AddMinutes(-5),
            EndedAt = endedAt,
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> PostCounterAsync(string? key, string? source = null) =>
        _client!.PostAsJsonAsync("/api/analytics/counter", new CounterRequestBody(key, source), JsonOptions);

    private async Task<int> RowCountAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.DailyCounters.CountAsync();
    }

    private async Task<DailyCounter> SingleRowAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.DailyCounters.SingleAsync();
    }

    private async Task<List<DailyCounter>> AllRowsAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.DailyCounters.ToListAsync();
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
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

    /// <summary>
    /// Same shape as ModerationEndpointTests.RegisterAndPromoteAsync: promotes through UserManager
    /// then re-logs-in through the real endpoint (with 2FA, since ModerationPolicies.Admin requires
    /// the mfa claim too) so the returned token genuinely carries the role claim.
    /// </summary>
    private async Task<AuthResult> RegisterAndPromoteAsync(string email, string displayName)
    {
        var registered = await RegisterAsync(email, displayName);

        using (var scope = _factory!.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roles.RoleExistsAsync(ModerationRoles.Admin))
                await roles.CreateAsync(new IdentityRole<Guid>(ModerationRoles.Admin));
            var user = await users.FindByIdAsync(registered.UserId.ToString());
            await users.AddToRoleAsync(user!, ModerationRoles.Admin);
        }

        var sharedKey = await TotpTestHelper.EnableTwoFactorAsync(_factory!.Services, registered.UserId);
        var loginResponse = await TotpTestHelper.LoginWithTwoFactorAsync(
            _client!, email, Password, sharedKey, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
        var relogged = await loginResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(relogged);
        return relogged!;
    }

    private sealed record CounterRequestBody(string? Key, string? Source);

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);
}
