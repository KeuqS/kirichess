using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
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
/// End-to-end proof of the Phase 5 exit criterion: finishing a rated, two-distinct-player game
/// writes Glicko-2 ratings + history in the same transaction as the game, while solo/practice
/// games write none. Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class RatingPersistenceTests : IAsyncLifetime
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

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Rated_two_player_game_writes_ratings_and_history()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var opponent = await RegisterAsync("rating-opponent@example.com", "Rating Opponent");
        var me = await RegisterAsync("rating-player@example.com", "Rating Player");

        // Rated two-player games come from matchmaking (JoinQueue), not friend links. Tests create
        // the same shape through GameRegistry so ratings persist without spinning up the hub.
        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var session = await registry.CreateAsync(
            KiriChessConfig.Presets["steal1"].Clone(), me.UserId, opponent.UserId, "steal1", rated: true);
        var gameId = session.Id;

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);

        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();

        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var ratings = await db.Ratings.Where(r => r.Pool == "steal1").ToListAsync();
        Assert.Equal(2, ratings.Count);
        Assert.All(ratings, r => Assert.Equal(1, r.GamesPlayed));
        Assert.All(ratings, r => Assert.True(r.LastPlayedAt > DateTimeOffset.UtcNow.AddMinutes(-5)));

        var histories = await db.RatingHistories.Where(h => h.GameId == gameId).ToListAsync();
        Assert.Equal(2, histories.Count);

        var winner = ratings.Single(r => r.Value > 1500);
        var loser = ratings.Single(r => r.Value < 1500);
        Assert.NotEqual(winner.UserId, loser.UserId);
    }

    [SkippableFact]
    public async Task Solo_practice_game_writes_no_ratings()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("rating-solo@example.com", "Rating Solo");
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);

        var createResponse = await _client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "steal1",
            Rated = true
        }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(created);
        var gameId = created!.GameId;

        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();

        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        Assert.Empty(await db.Ratings.ToListAsync());
        Assert.Empty(await db.RatingHistories.Where(h => h.GameId == gameId).ToListAsync());
    }

    /// <summary>
    /// C-1-4. A second <see cref="RatingService.ApplyAsync"/> for a game that already has history
    /// must leave the rating and the history row alone. The in-memory session guard does not
    /// cover a restart that builds a new context and persists the same finished game again.
    /// </summary>
    [SkippableFact]
    public async Task Reapplying_ratings_for_the_same_game_does_not_double_count()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var opponent = await RegisterAsync("rating-reapply-opponent@example.com", "Reapply Opponent");
        var me = await RegisterAsync("rating-reapply-player@example.com", "Reapply Player");

        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var session = await registry.CreateAsync(
            KiriChessConfig.Presets["bounty1"].Clone(), me.UserId, opponent.UserId, "bounty1", rated: true);
        var gameId = session.Id;

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);
        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        (await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions))
            .EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/games/{gameId}/resign", content: null)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == gameId);
        var again = await scope.ServiceProvider.GetRequiredService<KiriChess.Server.Ratings.RatingService>()
            .ApplyAsync(game, db);
        Assert.Null(again);
        await db.SaveChangesAsync();

        var ratings = await db.Ratings.Where(r => r.Pool == "bounty1" && (r.UserId == me.UserId || r.UserId == opponent.UserId)).ToListAsync();
        Assert.Equal(2, ratings.Count);
        Assert.All(ratings, r => Assert.Equal(1, r.GamesPlayed));
        Assert.Equal(2, await db.RatingHistories.CountAsync(h => h.GameId == gameId));
    }

    /// <summary>
    /// C-1-4 / C-8-9. The schema, not the service, is what stops a second history row for the
    /// same game and user. Replay and PGN key that table by user id.
    /// </summary>
    [SkippableFact]
    public async Task A_second_history_row_for_the_same_game_and_user_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var opponent = await RegisterAsync("rating-unique-opponent@example.com", "Unique Opponent");
        var me = await RegisterAsync("rating-unique-player@example.com", "Unique Player");

        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var session = await registry.CreateAsync(
            KiriChessConfig.Presets["bounty1"].Clone(), me.UserId, opponent.UserId, "bounty1", rated: true);
        var gameId = session.Id;

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", me.Token);
        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        (await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions))
            .EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/games/{gameId}/resign", content: null)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.RatingHistories.Add(new RatingHistory
        {
            GameId = gameId,
            UserId = me.UserId,
            Pool = "bounty1",
            ValueBefore = 1500,
            ValueAfter = 1510,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// C-7-6. The finish's save uses the same retry as this test. Another writer commits the
    /// rating after it was read. xmin rejects the stale write, and the second attempt adds one
    /// game on top of 1234.5 rather than on the value it read first.
    /// </summary>
    [SkippableFact]
    public async Task A_stale_rating_write_is_recomputed_from_the_committed_row()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("rating-race-player@example.com", "Race Player");
        await using var seed = NewContext();
        seed.Ratings.Add(NewRating(me.UserId, "race-pool", 1500, gamesPlayed: 1));
        await seed.SaveChangesAsync();

        var attempts = 0;
        await using var db = NewContext();
        await RatingRowConflict.CommitAsync(db, async () =>
        {
            attempts++;
            if (attempts == 1)
            {
                var row = await db.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "race-pool");
                await using var side = NewContext();
                var planted = await side.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "race-pool");
                planted.Value = 1234.5;
                planted.GamesPlayed = 4;
                await side.SaveChangesAsync();
                row.GamesPlayed += 1;
                row.Value += 1;
            }
            else
            {
                var row = await db.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "race-pool");
                row.GamesPlayed += 1;
                row.Value += 1;
            }
        });

        Assert.Equal(2, attempts);
        await using var check = NewContext();
        var mine = await check.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "race-pool");
        Assert.Equal(5, mine.GamesPlayed);
        Assert.Equal(1235.5, mine.Value);
    }

    /// <summary>
    /// C-7-6. Two first games in one pool both try to insert the rating row. The unique index
    /// rejects the loser. The retry must rate the game on the row that won, not fail the finish.
    /// </summary>
    [SkippableFact]
    public async Task A_first_rating_insert_that_loses_the_race_is_retried()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("rating-insert-player@example.com", "Insert Player");

        var attempts = 0;
        await using var db = NewContext();
        await RatingRowConflict.CommitAsync(db, async () =>
        {
            attempts++;
            if (attempts == 1)
            {
                db.Ratings.Add(NewRating(me.UserId, "insert-pool", 1500, gamesPlayed: 0));
                await using var side = NewContext();
                side.Ratings.Add(NewRating(me.UserId, "insert-pool", 1234.5, gamesPlayed: 0));
                await side.SaveChangesAsync();
            }
            else
            {
                var row = await db.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "insert-pool");
                row.GamesPlayed += 1;
                row.Value += 1;
            }
        });

        Assert.Equal(2, attempts);
        await using var check = NewContext();
        var mine = await check.Ratings.SingleAsync(r => r.UserId == me.UserId && r.Pool == "insert-pool");
        Assert.Equal(1, mine.GamesPlayed);
        Assert.Equal(1235.5, mine.Value);
    }

    private KiriChessDbContext NewContext()
    {
        var scope = _factory!.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
    }

    private static Rating NewRating(Guid userId, string pool, double value, int gamesPlayed) => new()
    {
        UserId = userId,
        Pool = pool,
        Value = value,
        RatingDeviation = 350,
        Volatility = 0.06,
        GamesPlayed = gamesPlayed,
    };

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

    private sealed record GameStateDto(
        Guid GameId,
        string Fen,
        string SideToMove,
        int WhiteTime,
        int BlackTime,
        string Status,
        string? Winner,
        string Termination,
        Guid WhitePlayerId,
        Guid BlackPlayerId,
        int MoveCount);
}
