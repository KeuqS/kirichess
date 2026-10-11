using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end coverage of <c>GET /api/games/{id}/replay</c> (Phase 8.2): the anonymous read path,
/// and, the load-bearing case, that an <c>Ongoing</c> game 404s exactly like an unknown id, so a
/// live game's move list can never be scraped through this route. Requires Docker; skips cleanly
/// (via <see cref="SkippableFactAttribute"/>) when no Docker daemon is reachable.
/// </summary>
public sealed class ReplayEndpointTests : IAsyncLifetime
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

        // The raw, un-authorized factory client: no AuthenticationHeaderValue is ever set on it,
        // which is itself part of what these tests are proving.
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_finished_game_replays_with_no_auth_header_required()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "replay-finished-white@example.com", "replay-finished-black@example.com",
            GameResult.WhiteWins, TerminationReason.Checkmate, movePlies: 2);

        var response = await _client!.GetAsync($"/api/games/{gameId}/replay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReplayDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(gameId, dto!.GameId);
        Assert.Equal(2, dto.Plies.Length);
        Assert.Equal("WhiteWins", dto.Result);
        Assert.Equal("Checkmate", dto.Termination);
        // C-8-4: a finished game's replay is a pure function of an immutable row, so it is cacheable
        // for a long time and shares one link's worth of hits across every viewer via Cloudflare
        // rather than hitting Fly/Neon on every view.
        Assert.Equal("public, max-age=86400, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal($"W/\"{gameId:N}\"", response.Headers.ETag?.ToString());
    }

    [SkippableFact]
    public async Task A_finished_game_downloads_a_pgn_file()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "pgn-finished-white@example.com", "pgn-finished-black@example.com",
            GameResult.WhiteWins, TerminationReason.Checkmate, movePlies: 2);

        var response = await _client!.GetAsync($"/api/games/{gameId}/pgn");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-chess-pgn", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("[Event \"", body);
        // C-8-4, PGN half of the same fix.
        Assert.Equal("public, max-age=86400, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal($"W/\"{gameId:N}\"", response.Headers.ETag?.ToString());
    }

    [SkippableFact]
    public async Task An_ongoing_games_replay_carries_no_cache_headers()
    {
        // The 404 branch returns before ApplyImmutableCacheHeaders is ever reached; this pins that,
        // since the whole point of the header is that the response it decorates can never change -
        // which is exactly not true of a game that might finish a second from now.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "replay-ongoing-cache-white@example.com", "replay-ongoing-cache-black@example.com",
            GameResult.Ongoing, TerminationReason.None, movePlies: 2);

        var response = await _client!.GetAsync($"/api/games/{gameId}/replay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
        Assert.False(response.Headers.Contains("ETag"));
    }

    [Fact]
    public void ApplyImmutableCacheHeaders_sets_a_long_lived_Cache_Control_and_a_weak_ETag_from_the_game_id()
    {
        // Docker-free unit coverage of the helper itself, independent of the two Docker-gated tests
        // above that prove it is actually wired into both routes.
        var http = new DefaultHttpContext();
        var id = Guid.NewGuid();

        ReplayEndpoints.ApplyImmutableCacheHeaders(http, id);

        Assert.Equal("public, max-age=86400, immutable", http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"W/\"{id:N}\"", http.Response.Headers.ETag.ToString());
    }

    [SkippableFact]
    public async Task An_ongoing_game_has_no_pgn_download_so_a_live_move_list_cannot_be_scraped_that_way()
    {
        // This is the security assertion, not a smoke test: without it, the PGN route would be a
        // live-move-list leak through a second door, the same one Ongoing_game_is_not_replayable
        // below closes for the JSON replay. It costs a Docker container and is not optional.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "pgn-ongoing-white@example.com", "pgn-ongoing-black@example.com",
            GameResult.Ongoing, TerminationReason.None, movePlies: 2);

        var response = await _client!.GetAsync($"/api/games/{gameId}/pgn");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Ongoing_game_is_not_replayable_so_a_live_move_list_cannot_be_scraped()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "replay-ongoing-white@example.com", "replay-ongoing-black@example.com",
            GameResult.Ongoing, TerminationReason.None, movePlies: 2);

        var response = await _client!.GetAsync($"/api/games/{gameId}/replay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unknown_id_returns_404_indistinguishable_from_an_ongoing_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync($"/api/games/{Guid.NewGuid()}/replay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_draw_is_replayable()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "replay-draw-white@example.com", "replay-draw-black@example.com",
            GameResult.Draw, TerminationReason.DrawByAgreement, movePlies: 0);

        var response = await _client!.GetAsync($"/api/games/{gameId}/replay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_aborted_game_is_replayable()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var gameId = await SeedGameAsync(
            "replay-aborted-white@example.com", "replay-aborted-black@example.com",
            GameResult.Aborted, TerminationReason.ServerRestart, movePlies: 0);

        var response = await _client!.GetAsync($"/api/games/{gameId}/replay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    /// <summary>Inserts a <see cref="Game"/> row straight through the <see cref="KiriChessDbContext"/>,
    /// the replay endpoint reads finished rows written by <c>LiveGamePersister</c>/<c>GameSession</c>,
    /// neither of which needs to run for this test. Player ids must reference real registered users:
    /// <c>Game.WhitePlayerId</c>/<c>BlackPlayerId</c> carry a foreign key into AspNetUsers.</summary>
    private async Task<Guid> SeedGameAsync(
        string whiteEmail, string blackEmail, GameResult result, TerminationReason termination, int movePlies)
    {
        var white = await RegisterAsync(whiteEmail, "White Player");
        var black = await RegisterAsync(blackEmail, "Black Player");

        var config = KiriChessConfig.Presets["steal1"].Clone();
        var engine = new KiriChessEngine();
        var moves = new List<MoveRecord>();
        var position = engine.NewGame(config);
        var uciSequence = new[] { "e2e4", "e7e5", "g1f3", "b8c6" };
        var mover = Side.White;
        for (var i = 0; i < movePlies; i++)
        {
            var apply = engine.Apply(position, Move.Parse(uciSequence[i]), config, 0);
            position = apply.NewPosition;
            moves.Add(new MoveRecord(
                i + 1, uciSequence[i], mover,
                position.WhiteTimeSeconds, position.BlackTimeSeconds,
                apply.WhiteTimeDelta, apply.BlackTimeDelta, "Ongoing"));
            mover = mover.Opposite();
        }

        var game = new Game
        {
            WhitePlayerId = white.UserId,
            BlackPlayerId = black.UserId,
            Result = result,
            Termination = termination,
            RatingPool = "steal1",
            IsRated = true,
            StartFen = KiriChessEngine.StartFen,
            ConfigSnapshot = config,
            Moves = moves,
        };

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game.Id;
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record ReplayPlyDto(
        int Ply, string Uci, string Mover, string FenAfter,
        int WhiteTimeAfter, int BlackTimeAfter, int WhiteDelta, int BlackDelta);

    private sealed record ReplayDto(
        Guid GameId, string StartFen, Guid WhitePlayerId, Guid BlackPlayerId,
        string WhitePlayerName, string BlackPlayerName, string Result, string Termination,
        string? RatingPool, bool IsRated, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
        ReplayPlyDto[] Plies);
}
