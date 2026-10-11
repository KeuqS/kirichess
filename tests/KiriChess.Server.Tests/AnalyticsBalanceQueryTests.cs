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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The half of the analytics surface that <see cref="BalanceAnalyticsTests"/> and
/// <see cref="RetentionAnalyticsTests"/> cannot reach: proves the parts that only exist against a
/// real Postgres and a real login, including that AD.6+11a.3's admin-role gate actually authorizes
/// (not just that the maths is right), and that no user id leaves <c>/api/analytics/retention</c>.
///
/// <list type="bullet">
/// <item>the <c>AddGamePlyCount</c> migration applies, including its hand-written
/// <c>jsonb_array_length</c> backfill;</item>
/// <item>the endpoints' projections into <see cref="GameFact"/> / <see cref="SignupFact"/> /
/// <see cref="GameParticipationFact"/>, all <c>readonly record struct</c>s, actually translate to SQL.
/// They compile either way; an untranslatable projection is a runtime exception, so compiling proves
/// nothing here.</item>
/// </list>
///
/// Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class AnalyticsBalanceQueryTests : IAsyncLifetime
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

        var connectionString = _pg.GetConnectionString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Summary_is_computed_from_real_rows_with_excluded_games_left_out()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("balance-admin@example.com", "Balance Admin");
        var white = await RegisterAsync("balance-1@example.com", "Balance White");
        var black = await RegisterAsync("balance-2@example.com", "Balance Black");

        var endedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal1", plies: 30, endedAt);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal1", plies: 50, endedAt);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.BlackWins, TerminationReason.Flagfall, "steal1", plies: 40, endedAt);
        // Excluded: counted separately, never in a percentage.
        await SeedGameAsync(white.UserId, black.UserId, GameResult.Aborted, TerminationReason.ServerRestart, "steal1", plies: 5, endedAt);
        // A different pool, so the per-pool bucketing is exercised rather than assumed.
        await SeedGameAsync(white.UserId, black.UserId, GameResult.Draw, TerminationReason.Stalemate, "bounty1", plies: 60, endedAt);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/analytics/balance?sinceDays=30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<BalanceSummaryDto>(JsonOptions);
        Assert.NotNull(summary);

        Assert.Equal(4, summary!.TotalGames);      // 5 seeded, 1 excluded
        Assert.Equal(1, summary.ExcludedGames);
        Assert.False(summary.Truncated);

        var steal1 = Assert.Single(summary.Pools, p => p.Pool == "steal1");
        Assert.Equal(3, steal1.Games);
        Assert.Equal(1, steal1.ExcludedGames);
        Assert.Equal(66.7, steal1.CheckmatePercent);   // 2 of 3
        Assert.Equal(33.3, steal1.FlagfallPercent);    // 1 of 3
        Assert.Equal(66.7, steal1.WhiteWinPercent);
        Assert.Equal(40, steal1.AvgPlies);             // (30 + 50 + 40) / 3, proves PlyCount round-trips
        Assert.Equal(40, steal1.MedianPlies);

        // Official-pool ordering survives the round trip: bounty1 precedes steal1 in OfficialPools.
        Assert.Equal(new[] { "bounty1", "steal1" }, summary.Pools.Select(p => p.Pool));
    }

    [SkippableFact]
    public async Task Self_play_games_are_excluded_from_the_balance_summary()
    {
        // C-7-5: POST /api/games no longer writes IsRated=true/an official pool onto a self-play row
        // (C-6-5), but that fix only reaches rows created after it landed. Any same-account row
        // already in the database - or hand-seeded, as here - must still not be counted, or a script
        // can inflate a pool's win/draw/game-length statistics at nothing more than the GameCreate
        // rate limit. Matches the exclusion EngineDetectionEndpoints.cs already applies explicitly.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("balance-selfplay-admin@example.com", "SelfPlay Admin");
        var solo = await RegisterAsync("balance-selfplay-solo@example.com", "SelfPlay Solo");
        var white = await RegisterAsync("balance-selfplay-white@example.com", "SelfPlay White");
        var black = await RegisterAsync("balance-selfplay-black@example.com", "SelfPlay Black");

        var endedAt = DateTimeOffset.UtcNow.AddHours(-1);
        // Same account on both seats - exactly the shape GameEndpoints used to persist as
        // IsRated=true with an official pool before C-6-5 - carrying an implausible ply count that
        // would visibly skew the average if it were ever let in.
        await SeedGameAsync(solo.UserId, solo.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal1", plies: 999, endedAt);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal1", plies: 30, endedAt);

        AuthorizeAs(admin);
        var summary = await _client!.GetFromJsonAsync<BalanceSummaryDto>("/api/analytics/balance?sinceDays=30", JsonOptions);

        Assert.Equal(1, summary!.TotalGames); // the self-play row is excluded entirely, not merely uncounted
        var steal1 = Assert.Single(summary.Pools, p => p.Pool == "steal1");
        Assert.Equal(1, steal1.Games);
        Assert.Equal(30, steal1.AvgPlies); // the self-play row's 999 plies never entered the average
    }

    [SkippableFact]
    public async Task Rated_only_filter_is_applied_in_the_query()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("balance-rated-admin@example.com", "Rated Admin");
        var white = await RegisterAsync("balance-rated-1@example.com", "Rated White");
        var black = await RegisterAsync("balance-rated-2@example.com", "Rated Black");

        var endedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal2", plies: 20, endedAt);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, pool: null, plies: 20, endedAt, isRated: false);

        AuthorizeAs(admin);

        var all = await _client!.GetFromJsonAsync<BalanceSummaryDto>("/api/analytics/balance?sinceDays=30", JsonOptions);
        var ratedOnly = await _client.GetFromJsonAsync<BalanceSummaryDto>("/api/analytics/balance?sinceDays=30&ratedOnly=true", JsonOptions);

        Assert.Equal(2, all!.TotalGames);
        Assert.Contains(all.Pools, p => p.Pool == BalanceAnalytics.CustomPool);

        Assert.Equal(1, ratedOnly!.TotalGames);
        Assert.DoesNotContain(ratedOnly.Pools, p => p.Pool == BalanceAnalytics.CustomPool);
    }

    [SkippableFact]
    public async Task Games_outside_the_window_are_not_counted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("balance-window-admin@example.com", "Window Admin");
        var white = await RegisterAsync("balance-window-1@example.com", "Window White");
        var black = await RegisterAsync("balance-window-2@example.com", "Window Black");

        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate,
            "burn1", plies: 20, DateTimeOffset.UtcNow.AddDays(-100));

        AuthorizeAs(admin);
        var summary = await _client!.GetFromJsonAsync<BalanceSummaryDto>("/api/analytics/balance?sinceDays=30", JsonOptions);

        Assert.Equal(0, summary!.TotalGames);
        Assert.Empty(summary.Pools);
    }

    [SkippableFact]
    public async Task Non_admin_gets_403_on_balance_and_retention()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var regular = await RegisterAsync("analytics-non-admin@example.com", "Regular");
        AuthorizeAs(regular);

        var balanceResponse = await _client!.GetAsync("/api/analytics/balance");
        var retentionResponse = await _client.GetAsync("/api/analytics/retention");

        Assert.Equal(HttpStatusCode.Forbidden, balanceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retentionResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_sees_retention_funnel_and_no_user_id_leaks_in_the_response()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("retention-admin@example.com", "Retention Admin");
        var player1 = await RegisterAsync("retention-player1@example.com", "Retention Player One");
        var player2 = await RegisterAsync("retention-player2@example.com", "Retention Player Two");
        var bystander = await RegisterAsync("retention-bystander@example.com", "Retention Bystander");

        var now = DateTimeOffset.UtcNow;
        // Two games between the same pair, so both players reach the second-game step; the admin and
        // the bystander appear in no game at all.
        await SeedGameAsync(player1.UserId, player2.UserId, GameResult.WhiteWins, TerminationReason.Checkmate,
            "steal1", plies: 30, now.AddHours(-2));
        await SeedGameAsync(player1.UserId, player2.UserId, GameResult.WhiteWins, TerminationReason.Checkmate,
            "steal1", plies: 40, now.AddHours(-1));

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/analytics/retention?sinceDays=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // README scope decision 7: aggregate counts only, never a user id, display name or email.
        Assert.DoesNotContain(player1.UserId.ToString(), body);
        Assert.DoesNotContain(player2.UserId.ToString(), body);
        Assert.DoesNotContain(bystander.UserId.ToString(), body);
        Assert.DoesNotContain(admin.UserId.ToString(), body);

        var summary = JsonSerializer.Deserialize<RetentionSummaryDto>(body, JsonOptions);
        Assert.NotNull(summary);

        // admin + player1 + player2 + bystander = 4 signups this run; only the two players ever start
        // or finish a game, so the funnel is exactly "4 signed up, 2 played a game, 2 played a second".
        Assert.Equal(4, summary!.TotalSignups);
        Assert.Equal(4, summary.Funnel.Single(f => f.Step == FunnelSteps.Signup).Count);
        Assert.Equal(2, summary.Funnel.Single(f => f.Step == FunnelSteps.FirstGame).Count);
        Assert.Equal(2, summary.Funnel.Single(f => f.Step == FunnelSteps.SecondGame).Count);
    }

    [SkippableFact]
    public async Task Bot_pool_games_do_not_count_as_a_human_return_and_custom_games_do()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("retention-bot-admin@example.com", "Bot Filter Admin");
        var botWhite = await RegisterAsync("retention-bot-white@example.com", "Bot White");
        var botBlack = await RegisterAsync("retention-bot-black@example.com", "Bot Black");
        var customWhite = await RegisterAsync("retention-custom-white@example.com", "Custom White");
        var customBlack = await RegisterAsync("retention-custom-black@example.com", "Custom Black");

        var now = DateTimeOffset.UtcNow;
        await SeedGameAsync(botWhite.UserId, botBlack.UserId, GameResult.WhiteWins, TerminationReason.Checkmate,
            "bot:steal1", plies: 12, now.AddHours(-1));
        await SeedGameAsync(customWhite.UserId, customBlack.UserId, GameResult.Draw, TerminationReason.DrawByAgreement,
            null, 8, now.AddMinutes(-30), isRated: false);

        AuthorizeAs(admin);
        var summary = await _client!.GetFromJsonAsync<RetentionSummaryDto>(
            "/api/analytics/retention?sinceDays=30", JsonOptions);

        // Five signups. The bot-pool pair never played a human game. The custom pair did.
        Assert.Equal(5, summary!.TotalSignups);
        Assert.Equal(2, summary.Funnel.Single(f => f.Step == FunnelSteps.FirstGame).Count);
    }

    private async Task SeedGameAsync(
        Guid whiteId, Guid blackId, GameResult result, TerminationReason termination,
        string? pool, int plies, DateTimeOffset endedAt, bool isRated = true)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = result,
            Termination = termination,
            RatingPool = pool,
            IsRated = isRated,
            StartFen = string.Empty,
            ConfigSnapshot = new KiriChessConfig(),
            Moves = new List<MoveRecord>(),
            PlyCount = plies,
            CreatedAt = endedAt.AddMinutes(-5),
            EndedAt = endedAt,
        });
        await db.SaveChangesAsync();
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
    /// Registers a user, adds them to the Admin role directly through UserManager (the same thing
    /// ModerationSeeder does at boot), then logs back in through the real endpoint so the returned
    /// token actually carries the role claim - the point is proving the round trip, not hand-minting a
    /// token that merely looks right. Copied verbatim from ModerationEndpointTests.RegisterAndPromoteAsync
    /// per the plan, including its 9d.2 2FA enrolment (ModerationPolicies.Admin requires the mfa claim
    /// on top of the role).
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

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);
}
