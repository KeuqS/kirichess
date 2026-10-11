using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end coverage of <c>GET /api/ratings/users/{userId}</c>. Fixture scaffolding copied verbatim
/// from <see cref="ReplayEndpointTests"/>. Requires Docker; skips cleanly when no Docker daemon is
/// reachable.
/// </summary>
public sealed class RatingEndpointsTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "rating-endpoints-test-signing-key-at-least-32-bytes",
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
    public async Task Public_ratings_route_excludes_bot_pools()
    {
        // C-7-8: this route was the one place on the surface with no pool filter at all, while the
        // leaderboard (RatingEndpoints.cs:47) and the per-pool history endpoint (:110) both reject a
        // bot pool outright, and the bot API (PublicApiEndpoints.cs:196-198) deliberately filters the
        // other way. Not a disclosure - the pool name is not secret - but the three endpoints used to
        // disagree about whether a bot:{preset} row is part of the public human surface.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("rating-endpoints-official-and-bot@example.com", "Rating Test User");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.Ratings.Add(new Rating { UserId = user.UserId, Pool = "steal1", Value = 1500, RatingDeviation = 350 });
            db.Ratings.Add(new Rating { UserId = user.UserId, Pool = "bot:steal1", Value = 1600, RatingDeviation = 200 });
            await db.SaveChangesAsync();
        }

        var response = await _client!.GetFromJsonAsync<RatingDto[]>(
            $"/api/ratings/users/{user.UserId}", JsonOptions);

        Assert.NotNull(response);
        var pools = response!.Select(r => r.Pool).ToArray();
        Assert.Contains("steal1", pools);
        Assert.DoesNotContain("bot:steal1", pools);
        Assert.Single(response);
    }

    // C-7-1. The default board is the established one. A settled rating that has not been played
    // inside the active window used to stay at the top forever, because deviation inflation takes
    // months to cross the provisional threshold. includeProvisional still lists them: that flag
    // means "everyone with a rating", not "established".
    [SkippableFact]
    public async Task Established_leaderboard_omits_a_player_outside_the_active_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var active = await RegisterAsync("leaderboard-active@example.com", "Active Player");
        var departed = await RegisterAsync("leaderboard-departed@example.com", "Departed Player");
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.Ratings.Add(new Rating
            {
                UserId = active.UserId,
                Pool = "steal1",
                Value = 1600,
                RatingDeviation = 50,
                LastPlayedAt = now,
            });
            db.Ratings.Add(new Rating
            {
                UserId = departed.UserId,
                Pool = "steal1",
                Value = 1900,
                RatingDeviation = 50,
                LastPlayedAt = now - RatingConstants.LeaderboardActiveWindow - TimeSpan.FromDays(1),
            });
            await db.SaveChangesAsync();
        }

        var established = await _client!.GetFromJsonAsync<LeaderboardEntryDto[]>(
            "/api/leaderboards/steal1?limit=10", JsonOptions);
        Assert.NotNull(established);
        Assert.Contains(established!, row => row.UserId == active.UserId);
        Assert.DoesNotContain(established!, row => row.UserId == departed.UserId);

        var everyone = await _client!.GetFromJsonAsync<LeaderboardEntryDto[]>(
            "/api/leaderboards/steal1?limit=10&includeProvisional=true", JsonOptions);
        Assert.NotNull(everyone);
        Assert.Contains(everyone!, row => row.UserId == departed.UserId);
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

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);
}
