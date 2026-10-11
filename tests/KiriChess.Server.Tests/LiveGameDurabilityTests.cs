using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 6c end-to-end proof of live-game durability: a game survives a restart because it was
/// written at ply 0 and re-snapshotted after every move, boot recovery replays it back into memory,
/// and an unresumed game is aborted rather than silently lost or handed to a winner. Requires
/// Docker; skips cleanly when no Docker daemon is reachable. Each test boots two
/// <see cref="WebApplicationFactory{TEntryPoint}"/> instances against the *same* Postgres
/// container, the first simulates the pre-restart process, the second the post-restart one.
/// </summary>
public sealed class LiveGameDurabilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Durable_from_ply_0()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var factory = CreateFactory(connectionString);
        await MigrateAsync(factory);
        var client = factory.CreateClient();

        var auth = await RegisterAsync(client, "durable-ply0@example.com", "Durable Ply0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var created = await CreateGameAsync(client, opponentId: null, preset: "bounty1", rated: false);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Games.SingleAsync(g => g.Id == created.GameId);

        Assert.Equal(GameResult.Ongoing, row.Result);
        Assert.Null(row.EndedAt);
    }

    [SkippableFact]
    public async Task Restart_recovery_round_trip()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        var factory1 = CreateFactory(connectionString);
        await MigrateAsync(factory1);
        var client1 = factory1.CreateClient();

        const string email = "restart-roundtrip@example.com";
        var auth1 = await RegisterAsync(client1, email, "Restart Roundtrip");
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1.Token);

        var created = await CreateGameAsync(client1, opponentId: null, preset: "bounty1", rated: false);
        var gameId = created.GameId;

        await PlayFirstLegalMoveAsync(client1, gameId);
        var move2 = await PlayFirstLegalMoveAsync(client1, gameId);
        var fenBeforeShutdown = move2.State.Fen;

        await factory1.DisposeAsync(); // Graceful shutdown: LiveGamePersister.StopAsync flushes the live session.

        await using var factory2 = CreateFactory(connectionString, new Dictionary<string, string?>
        {
            ["Games:ResumeGraceSeconds"] = "300" // Long enough that the abort sweep can't fire mid-test.
        });
        var client2 = factory2.CreateClient();
        var auth2 = await LoginAsync(client2, email);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth2.Token);

        var getResponse = await client2.GetAsync($"/api/games/{gameId}");
        getResponse.EnsureSuccessStatusCode();
        var state = await getResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(state);
        Assert.Equal(2, state!.MoveCount);
        Assert.Equal(fenBeforeShutdown, state.Fen);
        Assert.Equal("Ongoing", state.Status);

        var resignResponse = await client2.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        using var scope = factory2.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Games.SingleAsync(g => g.Id == gameId);

        Assert.Equal(GameResult.BlackWins, row.Result); // Solo self-play: SideOf always resolves the resigner to White.
        Assert.Equal(TerminationReason.Resignation, row.Termination);
        Assert.NotNull(row.EndedAt);
        Assert.Equal(2, row.Moves.Count);
    }

    [SkippableFact]
    public async Task Unresumed_games_abort_honestly()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        var factory1 = CreateFactory(connectionString);
        await MigrateAsync(factory1);
        var client1 = factory1.CreateClient();

        var auth1 = await RegisterAsync(client1, "unresumed@example.com", "Unresumed Player");
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1.Token);

        var created = await CreateGameAsync(client1, opponentId: null, preset: "bounty1", rated: false);
        var gameId = created.GameId;
        await PlayFirstLegalMoveAsync(client1, gameId);

        await factory1.DisposeAsync();

        await using var factory2 = CreateFactory(connectionString, new Dictionary<string, string?>
        {
            ["Games:ResumeGraceSeconds"] = "1"
        });

        // Accessing Services builds the host, which runs boot recovery and arms the resume-window
        // sweep. No hub connection is ever made for this game, so nobody is "home", the sweep must
        // abort it rather than leave it stuck Ongoing or hand out a win.
        Game? row = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = factory2.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            row = await db.Games.SingleAsync(g => g.Id == gameId);
            if (row.Result == GameResult.Aborted) break;
            await Task.Delay(250);
        }

        Assert.NotNull(row);
        Assert.Equal(GameResult.Aborted, row!.Result);
        Assert.Equal(TerminationReason.ServerRestart, row.Termination);
        Assert.NotNull(row.EndedAt);

        using var finalScope = factory2.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.Empty(await finalDb.RatingHistories.Where(h => h.GameId == gameId).ToListAsync());
    }

    [SkippableFact]
    public async Task Finished_games_stay_finished()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        var factory1 = CreateFactory(connectionString);
        await MigrateAsync(factory1);
        var client1 = factory1.CreateClient();

        const string email = "finished-stays-finished@example.com";
        var auth1 = await RegisterAsync(client1, email, "Finished Player");
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1.Token);

        var created = await CreateGameAsync(client1, opponentId: null, preset: "bounty1", rated: false);
        var gameId = created.GameId;

        var resignResponse = await client1.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        await factory1.DisposeAsync();

        await using var factory2 = CreateFactory(connectionString);
        var client2 = factory2.CreateClient();
        var auth2 = await LoginAsync(client2, email);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth2.Token);

        // P-3-1a changed this contract on purpose. A finished game is no longer 404 to the players
        // who were in it: FinishedGameReader serves it straight from Postgres without putting the
        // row back into GameRegistry, so a rematch mail landing on /game/{id} after the session was
        // already evicted renders the result instead of dead-ending. "Stays finished" is therefore
        // asserted on the state rather than on the status code, which is the stronger claim anyway:
        // the game comes back over, never resurrected as Ongoing.
        var getResponse = await client2.GetAsync($"/api/games/{gameId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var state = await getResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(state);
        Assert.NotEqual("Ongoing", state!.Status);
        Assert.Equal("Resignation", state.Termination);
        Assert.Equal("Black", state.Winner);

        // The durable read is participant-gated inside FinishedGameReader, and that gate is the one
        // line of the change with a security consequence: without it, every finished game on the
        // instance becomes readable by id. A stranger must still get 404.
        var strangerClient = factory2.CreateClient();
        var stranger = await RegisterAsync(
            strangerClient, "finished-stays-finished-stranger@example.com", "Finished Stranger");
        strangerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", stranger.Token);
        var strangerResponse = await strangerClient.GetAsync($"/api/games/{gameId}");
        Assert.Equal(HttpStatusCode.NotFound, strangerResponse.StatusCode);

        using var scope = factory2.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var count = await db.Games.CountAsync(g => g.Id == gameId);
        Assert.Equal(1, count);

        var row = await db.Games.SingleAsync(g => g.Id == gameId);
        Assert.Equal(GameResult.BlackWins, row.Result);
        Assert.Equal(TerminationReason.Resignation, row.Termination);
    }

    [SkippableFact]
    public async Task Batched_snapshots_write_every_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var factory = CreateFactory(connectionString, new Dictionary<string, string?>
        {
            ["Persistence:SnapshotBatchSize"] = "8" // All eight games land in one round trip.
        });
        await MigrateAsync(factory);
        var client = factory.CreateClient();

        var auth = await RegisterAsync(client, "batched-snapshots@example.com", "Batched Snapshots");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        // One account per game. C-6-5 gave POST /api/games the already-in-a-game gate every other
        // creation path already had, so one account can no longer hold eight live games at once. What
        // this test is about, eight pending snapshots landing in a single round trip, is unchanged.
        var gameIds = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var player = await RegisterAsync(client, $"batched-snapshots-{i}@example.com", $"Batched Snapshots {i}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
            var created = await CreateGameAsync(client, opponentId: null, preset: "bounty1", rated: false);
            await PlayFirstLegalMoveAsync(client, created.GameId);
            gameIds.Add(created.GameId);
        }

        var persister = factory.Services.GetRequiredService<LiveGamePersister>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (persister.PendingSnapshotCount > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250);
        Assert.Equal(0, persister.PendingSnapshotCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        foreach (var gameId in gameIds)
        {
            var row = await db.Games.SingleAsync(g => g.Id == gameId);
            Assert.Empty(row.Moves);
            Assert.Equal(0, row.PlyCount);
            Assert.Equal(1, await db.GameMoves.CountAsync(m => m.GameId == gameId));
        }
    }

    [SkippableFact]
    public async Task Snapshot_write_touches_only_the_move_columns()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var factory = CreateFactory(connectionString);
        await MigrateAsync(factory);
        var client = factory.CreateClient();

        var auth = await RegisterAsync(client, "snapshot-columns@example.com", "Snapshot Columns");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var created = await CreateGameAsync(client, opponentId: null, preset: "bounty1", rated: false);
        var gameId = created.GameId;

        string startFen;
        DateTimeOffset createdAt;
        Guid whitePlayerId;
        Guid blackPlayerId;
        using (var beforeScope = factory.Services.CreateScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var before = await beforeDb.Games.SingleAsync(g => g.Id == gameId);
            startFen = before.StartFen;
            createdAt = before.CreatedAt;
            whitePlayerId = before.WhitePlayerId;
            blackPlayerId = before.BlackPlayerId;
        }

        await PlayFirstLegalMoveAsync(client, gameId);

        // LiveGamePersister.ExecuteAsync removes an entry from _queued at the top of its drain loop,
        // before it awaits the write, so PendingSnapshotCount reaching 0 only means the batch was
        // dequeued, not that it committed. Poll the row itself instead of the queue depth, so this
        // assertion proves the write landed rather than racing it.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Game after;
        while (true)
        {
            // Fresh scope on every poll, so nothing is served from a stale change tracker.
            using var afterScope = factory.Services.CreateScope();
            var afterDb = afterScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            after = await afterDb.Games.SingleAsync(g => g.Id == gameId);
            if (await afterDb.GameMoves.AnyAsync(m => m.GameId == gameId) || DateTimeOffset.UtcNow >= deadline)
                break;
            await Task.Delay(250);
        }

        Assert.Empty(after.Moves);
        Assert.Equal(0, after.PlyCount);
        using (var movesScope = factory.Services.CreateScope())
        {
            var movesDb = movesScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            Assert.Equal(1, await movesDb.GameMoves.CountAsync(m => m.GameId == gameId));
        }
        Assert.Equal(startFen, after.StartFen);
        Assert.NotEmpty(after.StartFen);
        Assert.Equal(createdAt, after.CreatedAt);
        Assert.Equal(whitePlayerId, after.WhitePlayerId);
        Assert.Equal(blackPlayerId, after.BlackPlayerId);
        Assert.Equal(GameResult.Ongoing, after.Result);
        Assert.Equal(TerminationReason.None, after.Termination);
        Assert.Null(after.EndedAt);
    }

    [SkippableFact]
    public async Task A_missing_row_does_not_lose_the_rest_of_the_batch()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        var factory1 = CreateFactory(connectionString, new Dictionary<string, string?>
        {
            ["Persistence:SnapshotBatchSize"] = "8" // All three land in one batch.
        });
        await MigrateAsync(factory1);
        var client1 = factory1.CreateClient();

        var auth1 = await RegisterAsync(client1, "missing-row-batch@example.com", "Missing Row Batch");
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1.Token);

        // One account per game, for the same reason as Batched_snapshots_write_every_game above. The
        // token is kept alongside the id because the second move below has to be played by the player
        // who owns that particular game; B11's participant guard 404s anyone else.
        var gameIds = new List<Guid>();
        var gameTokens = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var player = await RegisterAsync(client1, $"missing-row-batch-{i}@example.com", $"Missing Row Batch {i}");
            client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
            var created = await CreateGameAsync(client1, opponentId: null, preset: "bounty1", rated: false);
            await PlayFirstLegalMoveAsync(client1, created.GameId);
            gameIds.Add(created.GameId);
            gameTokens.Add(player.Token);
        }

        var deletedGameId = gameIds[1];
        var survivingGameIds = new[] { gameIds[0], gameIds[2] };

        using (var deleteScope = factory1.Services.CreateScope())
        {
            var deleteDb = deleteScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var row = await deleteDb.Games.SingleAsync(g => g.Id == deletedGameId);
            deleteDb.Games.Remove(row);
            await deleteDb.SaveChangesAsync();
        }

        // A second move in each, so the surviving rows can only reach two plies through a write that
        // happened *after* the deletion, i.e. through a batch that contained the missing id. Whether
        // that batch is the steady-state one or StopAsync's shutdown flush does not matter and is
        // not raced on: either way the batch fails as one transaction and has to retry per game for
        // this assertion to hold.
        for (var i = 0; i < gameIds.Count; i++)
        {
            client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gameTokens[i]);
            await PlayFirstLegalMoveAsync(client1, gameIds[i]);
        }

        // Graceful shutdown: StopAsync batches every still-live session in one call. If a bad id
        // could throw out of the retry, this dispose is where it would surface.
        await factory1.DisposeAsync();

        await using var factory2 = CreateFactory(connectionString, new Dictionary<string, string?>
        {
            ["Games:ResumeGraceSeconds"] = "300" // Long enough that the abort sweep can't fire mid-test.
        });

        using var scope = factory2.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        foreach (var gameId in survivingGameIds)
        {
            var row = await db.Games.SingleAsync(g => g.Id == gameId);
            Assert.Empty(row.Moves);
            Assert.Equal(2, await db.GameMoves.CountAsync(m => m.GameId == gameId));
        }

        Assert.Empty(await db.Games.Where(g => g.Id == deletedGameId).ToListAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
            ["Jwt:Issuer"] = "kirichess",
            ["Jwt:Audience"] = "kirichess",
            ["Jwt:ExpiryMinutes"] = "60"
        };
        if (extra is not null)
            foreach (var (key, value) in extra)
                settings[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        });
    }

    private static async Task MigrateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        await db.Database.MigrateAsync();
    }

    private static async Task<AuthResult> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
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

    private static async Task<AuthResult> LoginAsync(HttpClient client, string email)
    {
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = "P@ssword123!"
        }, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
        var auth = await loginResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    private static async Task<GameStateDto> CreateGameAsync(
        HttpClient client, Guid? opponentId, string preset, bool rated)
    {
        var createResponse = await client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = opponentId,
            Preset = preset,
            Rated = rated
        }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<MoveResponseDto> PlayFirstLegalMoveAsync(HttpClient client, Guid gameId)
    {
        var legalResponse = await client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await client.PostAsJsonAsync(
            $"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();
        var parsed = await moveResponse.Content.ReadFromJsonAsync<MoveResponseDto>(JsonOptions);
        Assert.NotNull(parsed);
        return parsed!;
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

    private sealed record MoveResponseDto(GameStateDto State, int WhiteDelta, int BlackDelta, object[] Events, bool GameOver);
}
