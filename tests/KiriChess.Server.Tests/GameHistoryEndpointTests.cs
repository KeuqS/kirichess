using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// End-to-end coverage of <c>GET /api/games/mine</c> (Phase 9e.1): only the caller's own finished
/// games, ongoing games excluded (the same 8.2 rule <see cref="ReplayEndpointTests"/> pins), an
/// erased opponent (8a.2) still resolves to a name, and rated games carry the rating delta a player
/// needs to check a disputed change. Requires Docker; skips cleanly when no Docker daemon is
/// reachable. Fixture scaffolding is copied verbatim from <see cref="ReplayEndpointTests"/>.
/// </summary>
public sealed class GameHistoryEndpointTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "game-history-test-signing-key-at-least-32-bytes",
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
    public async Task Requires_authentication()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/games/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Lists_finished_games_on_both_sides_but_never_ongoing_ones()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-me@example.com", "History Me");
        var opponent = await RegisterAsync("history-opponent@example.com", "Opponent");

        var asWhite = await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, endedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var asBlack = await SeedGameAsync(opponent.UserId, me.UserId, GameResult.BlackWins, endedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedGameAsync(me.UserId, opponent.UserId, GameResult.Ongoing, endedAt: null);

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Length);
        Assert.DoesNotContain(rows, r => r.Result == "Ongoing");

        // Newest first.
        Assert.Equal(asBlack, rows[0].GameId);
        Assert.Equal("Black", rows[0].MySide);
        Assert.Equal(asWhite, rows[1].GameId);
        Assert.Equal("White", rows[1].MySide);
    }

    [SkippableFact]
    public async Task Never_shows_another_users_games()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-isolated-me@example.com", "Isolated Me");
        var strangerA = await RegisterAsync("history-stranger-a@example.com", "Stranger A");
        var strangerB = await RegisterAsync("history-stranger-b@example.com", "Stranger B");
        await SeedGameAsync(strangerA.UserId, strangerB.UserId, GameResult.Draw, DateTimeOffset.UtcNow);

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);
        Assert.Empty(rows!);
    }

    [SkippableFact]
    public async Task An_erased_opponent_still_resolves_to_a_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-tombstone-me@example.com", "Tombstone Me");
        var opponent = await RegisterAsync("history-tombstone-opponent@example.com", "Gone Player");
        await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, DateTimeOffset.UtcNow);

        // 8a.2 anonymises rather than deletes the row; simulate that in place.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == opponent.UserId);
            user.DisplayName = "Deleted account";
            user.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine");

        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);
        Assert.Single(rows!);
        Assert.Equal("Deleted account", rows![0].OpponentName);
    }

    [SkippableFact]
    public async Task Rated_games_carry_the_callers_rating_delta_unrated_games_do_not()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-rating-me@example.com", "Rating Me");
        var opponent = await RegisterAsync("history-rating-opponent@example.com", "Opponent");

        var ratedGameId = await SeedGameAsync(
            me.UserId, opponent.UserId, GameResult.WhiteWins, DateTimeOffset.UtcNow, rated: true, pool: "steal1");
        var unratedGameId = await SeedGameAsync(
            me.UserId, opponent.UserId, GameResult.Draw, DateTimeOffset.UtcNow.AddMinutes(-1), rated: false, pool: null);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.RatingHistories.Add(new RatingHistory
            {
                GameId = ratedGameId,
                UserId = me.UserId,
                Pool = "steal1",
                ValueBefore = 1500,
                ValueAfter = 1516,
            });
            await db.SaveChangesAsync();
        }

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine");
        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);

        var rated = Assert.Single(rows!, r => r.GameId == ratedGameId);
        Assert.Equal(1500, rated.RatingBefore);
        Assert.Equal(1516, rated.RatingAfter);

        var unrated = Assert.Single(rows!, r => r.GameId == unratedGameId);
        Assert.Null(unrated.RatingBefore);
        Assert.Null(unrated.RatingAfter);
    }

    [SkippableFact]
    public async Task PlyCount_survives_the_narrow_projection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // C-2-7: ListMineAsync now projects to the ten scalar columns the DTO uses before
        // materialising, rather than pulling whole Game entities (Moves and ConfigSnapshot jsonb
        // included) across the wire. PlyCount specifically exists so a consumer never has to
        // deserialise the move list at all; this pins that it still round-trips correctly through the
        // new .Select(...) rather than silently becoming 0.
        var me = await RegisterAsync("history-plycount-me@example.com", "Ply Count Me");
        var opponent = await RegisterAsync("history-plycount-opponent@example.com", "Opponent");
        var gameId = await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, DateTimeOffset.UtcNow, plyCount: 37);

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine");
        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);

        var row = Assert.Single(rows!, r => r.GameId == gameId);
        Assert.Equal(37, row.PlyCount);
    }

    [SkippableFact]
    public async Task Skip_and_take_page_through_history()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-paging-me@example.com", "Paging Me");
        var opponent = await RegisterAsync("history-paging-opponent@example.com", "Opponent");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, now.AddMinutes(-i));

        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine?take=2&skip=2");

        var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);
        Assert.Equal(2, rows!.Length);
    }

    [SkippableFact]
    public async Task Keyset_pages_cover_every_game_once_including_tied_end_times()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-keyset-me@example.com", "Keyset Me");
        var opponent = await RegisterAsync("history-keyset-opponent@example.com", "Opponent");
        var tied = DateTimeOffset.UtcNow.AddMinutes(-30);
        var ids = new List<Guid>
        {
            await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, DateTimeOffset.UtcNow.AddMinutes(-1)),
            await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, tied),
            await SeedGameAsync(opponent.UserId, me.UserId, GameResult.BlackWins, tied),
            await SeedGameAsync(me.UserId, opponent.UserId, GameResult.Draw, DateTimeOffset.UtcNow.AddHours(-2)),
        };

        AuthorizeAs(me);
        var seen = new List<Guid>();
        string? url = "/api/games/mine?take=2";
        for (var page = 0; page < 5 && url is not null; page++)
        {
            var response = await _client!.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = await response.Content.ReadFromJsonAsync<GameHistoryDto[]>(JsonOptions);
            Assert.NotNull(rows);
            // A full page does not say whether another row exists, so the walk asks once more
            // and the answer is an empty page. That empty page is the end, not a miss.
            if (rows!.Length == 0)
            {
                Assert.NotEmpty(seen);
                break;
            }
            seen.AddRange(rows.Select(r => r.GameId));
            url = rows.Length < 2
                ? null
                : "/api/games/mine?take=2&before="
                    + Uri.EscapeDataString(rows[^1].EndedAt!.Value.ToString("o"))
                    + "&beforeId=" + rows[^1].GameId;
        }

        Assert.Equal(ids.OrderBy(id => id).ToArray(), seen.OrderBy(id => id).ToArray());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [SkippableFact]
    public async Task A_finished_game_with_no_end_time_is_omitted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-nullend-me@example.com", "Null End Me");
        var opponent = await RegisterAsync("history-nullend-opponent@example.com", "Opponent");
        await SeedGameAsync(me.UserId, opponent.UserId, GameResult.WhiteWins, endedAt: null);
        var listed = await SeedGameAsync(me.UserId, opponent.UserId, GameResult.Draw, DateTimeOffset.UtcNow);

        AuthorizeAs(me);
        var rows = await _client!.GetFromJsonAsync<GameHistoryDto[]>("/api/games/mine", JsonOptions);

        var row = Assert.Single(rows!);
        Assert.Equal(listed, row.GameId);
    }

    [SkippableFact]
    public async Task Before_without_beforeId_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("history-cursor-me@example.com", "Cursor Me");
        AuthorizeAs(me);
        var response = await _client!.GetAsync("/api/games/mine?before=2026-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        // Display names must clear ContentModerationOptions.MinLength (3); "Me" used to fail here with a
        // bare 400 thrown by this helper rather than by the endpoint under test.
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

    /// <summary>Inserts a <see cref="Game"/> row straight through the <see cref="KiriChessDbContext"/>,
    /// same shortcut <see cref="ReplayEndpointTests"/> uses: the history endpoint reads finished rows,
    /// nothing about how they got there.</summary>
    private async Task<Guid> SeedGameAsync(
        Guid whitePlayerId, Guid blackPlayerId, GameResult result, DateTimeOffset? endedAt,
        bool rated = true, string? pool = "steal1", int plyCount = 0)
    {
        var game = new Game
        {
            WhitePlayerId = whitePlayerId,
            BlackPlayerId = blackPlayerId,
            Result = result,
            Termination = result == GameResult.Ongoing ? TerminationReason.None : TerminationReason.Resignation,
            RatingPool = pool,
            IsRated = rated,
            StartFen = "startpos",
            EndedAt = endedAt,
            PlyCount = plyCount,
        };

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game.Id;
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record GameHistoryDto(
        Guid GameId, Guid OpponentId, string OpponentName, string MySide, string Result, string Termination,
        string? RatingPool, bool IsRated, int PlyCount, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
        double? RatingBefore, double? RatingAfter);
}
