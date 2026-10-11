using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end coverage of <c>GET /api/users/{id}</c>, the anonymous public profile read. Two of
/// these are correctness controls rather than smoke tests: an erased 8a.2 tombstone must 404 like
/// an unknown id (otherwise erasure publishes a scrubbed page instead of removing one), and a
/// lapsed patron must read as None (the endpoint has to go through PatronBadge's rule, not the raw
/// IsPatron column). Requires Docker; skips cleanly without it.
/// </summary>
public sealed class PublicProfileEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        // Never carries an Authorization header: that this route answers at all without one is
        // part of what these tests assert.
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_profile_reads_with_no_auth_header_required()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("profile-anon@example.com", "Anon Reader");

        var response = await _client!.GetAsync($"/api/users/{user.UserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(user.UserId, dto!.UserId);
        Assert.Equal("Anon Reader", dto.DisplayName);
        Assert.Equal(nameof(PatronTier.None), dto.PatronTier);
        Assert.Equal(0, dto.GamesPlayed);
        Assert.Empty(dto.Ratings);
        Assert.NotEqual(default, dto.JoinedAt);
        Assert.Null(dto.AvatarUrl);
    }

    [SkippableFact]
    public async Task An_unknown_id_returns_404()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync($"/api/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_erased_account_returns_404_so_erasure_does_not_publish_a_tombstone()
    {
        // The load-bearing one. An 8a.2 tombstone keeps the row so foreign keys stay valid; without
        // the DeletedAt filter this endpoint would render that scrubbed row as a profile page.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("profile-erased@example.com", "Erased Player");

        using (var scope = _factory!.Services.CreateScope())
        {
            var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
            var result = await deletion.DeleteAsync(user.UserId, CancellationToken.None);
            Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        }

        var response = await _client!.GetAsync($"/api/users/{user.UserId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Games_played_counts_finished_games_and_ignores_an_ongoing_one()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("profile-count-white@example.com", "Count White");
        var black = await RegisterAsync("profile-count-black@example.com", "Count Black");

        await SeedGameAsync(white.UserId, black.UserId, GameResult.WhiteWins, TerminationReason.Checkmate);
        await SeedGameAsync(black.UserId, white.UserId, GameResult.Draw, TerminationReason.DrawByAgreement);
        await SeedGameAsync(white.UserId, black.UserId, GameResult.Ongoing, TerminationReason.None);

        var response = await _client!.GetAsync($"/api/users/{white.UserId}");

        var dto = await response.Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.GamesPlayed);
    }

    [SkippableFact]
    public async Task Ratings_appear_for_every_pool_the_player_has_one_in()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("profile-ratings@example.com", "Rated Player");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.Ratings.Add(new Rating
            {
                UserId = user.UserId, Pool = "steal1", Value = 1600,
                RatingDeviation = 60, Volatility = 0.06, GamesPlayed = 30
            });
            db.Ratings.Add(new Rating
            {
                UserId = user.UserId, Pool = "burn1", Value = 1450,
                RatingDeviation = 200, Volatility = 0.06, GamesPlayed = 3
            });
            await db.SaveChangesAsync();
        }

        var response = await _client!.GetAsync($"/api/users/{user.UserId}");

        var dto = await response.Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.Ratings.Count);
        var steal = Assert.Single(dto.Ratings, r => r.Pool == "steal1");
        Assert.Equal(1600, steal.Rating);
        Assert.False(steal.Provisional);
        var burn = Assert.Single(dto.Ratings, r => r.Pool == "burn1");
        Assert.True(burn.Provisional);
    }

    [SkippableFact]
    public async Task A_patron_badge_shows_and_a_lapsed_one_does_not()
    {
        // Pins that the endpoint asks PatronBadge rather than reading IsPatron: a one-off badge
        // lapses on a date with no sweep to flip the column, so the raw bool is still true here.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var live = await RegisterAsync("profile-patron-live@example.com", "Live Patron");
        var lapsed = await RegisterAsync("profile-patron-lapsed@example.com", "Lapsed Patron");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var liveUser = await db.Users.FirstAsync(u => u.Id == live.UserId);
            liveUser.IsPatron = true;
            liveUser.PatronTier = PatronTier.Knight;
            liveUser.PatronKind = PatronKind.Monthly;
            liveUser.PatronSince = DateTimeOffset.UtcNow.AddDays(-30);

            var lapsedUser = await db.Users.FirstAsync(u => u.Id == lapsed.UserId);
            lapsedUser.IsPatron = true;
            lapsedUser.PatronTier = PatronTier.Knight;
            lapsedUser.PatronKind = PatronKind.OneTime;
            lapsedUser.PatronSince = DateTimeOffset.UtcNow.AddYears(-2);
            lapsedUser.PatronExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var liveDto = await (await _client!.GetAsync($"/api/users/{live.UserId}"))
            .Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        var lapsedDto = await (await _client!.GetAsync($"/api/users/{lapsed.UserId}"))
            .Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);

        Assert.Equal(nameof(PatronTier.Knight), liveDto!.PatronTier);
        Assert.Equal(nameof(PatronTier.None), lapsedDto!.PatronTier);
    }

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    /// <summary>A bare finished (or ongoing) row, enough for the games-played count; the move list
    /// is irrelevant to this endpoint, so no plies are written.</summary>
    private async Task SeedGameAsync(Guid whiteId, Guid blackId, GameResult result, TerminationReason termination)
    {
        var config = KiriChessConfig.Presets["steal1"].Clone();
        var game = new Game
        {
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = result,
            Termination = termination,
            RatingPool = "steal1",
            IsRated = true,
            StartFen = KiriChessEngine.StartFen,
            ConfigSnapshot = config,
            Moves = [],
        };

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(game);
        await db.SaveChangesAsync();
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);
}
