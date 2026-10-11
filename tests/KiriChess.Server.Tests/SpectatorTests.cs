using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8.1 spectator mode: the <see cref="SpectatorTracker"/> bookkeeping, the
/// <see cref="GameMapping.ToSpectatorSnapshot"/> mapping, the hub auth split, and the
/// <c>GET /api/games/live</c> endpoint.
/// </summary>
public sealed class SpectatorTests
{
    // --- 8a: SpectatorTracker ---
    // Phase 8.4 (Task C): every member below became async (a nullable Redis collaborator now backs
    // cross-instance counting), but with no collaborator supplied here the behaviour is byte-for-byte
    // the pre-8.4 local-only tracker, these tests are unchanged apart from the await.

    [Fact]
    public async Task Add_then_CountFor_reports_one_watcher()
    {
        var tracker = new SpectatorTracker();
        var game = Guid.NewGuid();

        await tracker.AddAsync("conn-1", game);

        Assert.Equal(1, await tracker.CountForAsync(game));
    }

    [Fact]
    public async Task Two_connections_on_one_game_count_as_two()
    {
        var tracker = new SpectatorTracker();
        var game = Guid.NewGuid();

        await tracker.AddAsync("conn-1", game);
        await tracker.AddAsync("conn-2", game);

        Assert.Equal(2, await tracker.CountForAsync(game));
    }

    [Fact]
    public async Task Re_adding_the_same_connection_to_the_same_game_is_a_no_op()
    {
        // This is the reconnect path: a client's onreconnected handler re-invokes JoinAsSpectator
        // with the same game id. A naive Add would double-count; it must not.
        var tracker = new SpectatorTracker();
        var game = Guid.NewGuid();

        await tracker.AddAsync("conn-1", game);
        var left = await tracker.AddAsync("conn-1", game);

        Assert.Null(left);
        Assert.Equal(1, await tracker.CountForAsync(game));
    }

    [Fact]
    public async Task Adding_a_connection_to_a_different_game_returns_the_old_id_and_drops_the_old_count()
    {
        var tracker = new SpectatorTracker();
        var gameA = Guid.NewGuid();
        var gameB = Guid.NewGuid();

        await tracker.AddAsync("conn-1", gameA);
        var left = await tracker.AddAsync("conn-1", gameB);

        Assert.Equal(gameA, left);
        Assert.Equal(0, await tracker.CountForAsync(gameA));
        Assert.Equal(1, await tracker.CountForAsync(gameB));
    }

    [Fact]
    public async Task Remove_returns_the_watched_game_then_null_on_a_second_call()
    {
        var tracker = new SpectatorTracker();
        var game = Guid.NewGuid();
        await tracker.AddAsync("conn-1", game);

        var first = await tracker.RemoveAsync("conn-1");
        var second = await tracker.RemoveAsync("conn-1");

        Assert.Equal(game, first);
        Assert.Null(second);
    }

    [Fact]
    public async Task Remove_of_an_unknown_connection_returns_null()
    {
        var tracker = new SpectatorTracker();

        Assert.Null(await tracker.RemoveAsync("never-added"));
    }

    [Fact]
    public async Task Counts_groups_watchers_by_game_in_a_single_pass()
    {
        var tracker = new SpectatorTracker();
        var gameA = Guid.NewGuid();
        var gameB = Guid.NewGuid();
        await tracker.AddAsync("conn-1", gameA);
        await tracker.AddAsync("conn-2", gameA);
        await tracker.AddAsync("conn-3", gameB);

        var counts = await tracker.CountsAsync(new[] { gameA, gameB });

        Assert.Equal(2, counts[gameA]);
        Assert.Equal(1, counts[gameB]);
    }

    // --- 8b: ToSpectatorSnapshot mapping ---

    [Fact]
    public void ToSpectatorSnapshot_echoes_names_move_log_and_spectator_count()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var clock = new FakeGameClock();
        var config = new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };
        var session = new GameSession(new KiriChessEngine(), config, white, black, "bounty1", isRated: true, clock);

        session.ApplyMove(white, Move.Parse("e2e4"));
        session.ApplyMove(black, Move.Parse("e7e5"));

        var snapshot = session.ToSpectatorSnapshot("Alice", "Bob", "None", "None", spectators: 3);

        Assert.Equal("Alice", snapshot.WhiteName);
        Assert.Equal("Bob", snapshot.BlackName);
        Assert.Equal("None", snapshot.WhitePatronTier);
        Assert.Equal("None", snapshot.BlackPatronTier);
        Assert.Equal(3, snapshot.Spectators);
        Assert.Equal(session.Id, snapshot.State.GameId);
        Assert.Equal(2, snapshot.Moves.Count);

        Assert.Equal(1, snapshot.Moves[0].Ply);
        Assert.Equal("e2e4", snapshot.Moves[0].Uci);
        Assert.Equal(Side.White.ToString(), snapshot.Moves[0].Mover);

        Assert.Equal(2, snapshot.Moves[1].Ply);
        Assert.Equal("e7e5", snapshot.Moves[1].Uci);
        Assert.Equal(Side.Black.ToString(), snapshot.Moves[1].Mover);

        // The point of this test: SpectatorMoveDto must not leak the 6e.2 anti-cheat telemetry
        // (ThinkMs/LagMs/ReceivedAtUtc) to an anonymous channel. Asserting on the declared property
        // set, rather than just trusting the record definition, is what makes that intent legible to
        // the next reader and catches a future field added without thinking about who receives it.
        var moveProperties = typeof(SpectatorMoveDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("ThinkMs", moveProperties);
        Assert.DoesNotContain("LagMs", moveProperties);
        Assert.DoesNotContain("ReceivedAtUtc", moveProperties);

        // C-2-9 = C-8-7: the join snapshot withholds the engine's legal-move list. The live feed
        // must match; that half is the Docker test below.
        var snapshotProperties = typeof(SpectatorSnapshotDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("LegalMoves", snapshotProperties);
    }

    // --- 8c: hub authorization guard ---

    /// <summary>
    /// Phase 8.1 moved GameHub's [Authorize] from the class to each player method so that
    /// JoinAsSpectator can be reached anonymously. The risk that trade introduces is a *future*
    /// method added without the attribute, which would be silently anonymous. This test is the thing
    /// that catches that: adding a hub method now forces an explicit decision about which side of the
    /// auth line it is on.
    /// </summary>
    [Fact]
    public void Every_client_invokable_hub_method_requires_authorization()
    {
        // JoinAsSpectator is anonymous by design (README § "Anonymous spectating").
        // OnDisconnectedAsync is a SignalR lifetime callback, not client-invokable.
        var anonymousByDesign = new[] { nameof(GameHub.JoinAsSpectator), "OnDisconnectedAsync" };

        var unattributed = typeof(GameHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !anonymousByDesign.Contains(m.Name))
            .Where(m => m.GetCustomAttribute<AuthorizeAttribute>() is null)
            .Select(m => m.Name)
            .ToArray();

        Assert.True(unattributed.Length == 0,
            $"These GameHub methods are reachable without a token: {string.Join(", ", unattributed)}. "
            + "Add [Authorize], or add the method to anonymousByDesign if that is intended.");
    }

    [Fact]
    public void JoinAsSpectator_does_not_carry_Authorize()
    {
        // The inverse of the guard above: if someone "helpfully" attributes JoinAsSpectator later,
        // this fails loudly instead of silently breaking logged-out spectating.
        var method = typeof(GameHub).GetMethod(nameof(GameHub.JoinAsSpectator));
        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<AuthorizeAttribute>());
    }

    /// <summary>
    /// C-2-6 leftover: JoinAsSpectator used to register with SpectatorTracker and join the SignalR
    /// group before JoinAsSpectatorAsync could throw "Game not found." A caller naming any Guid was
    /// counted. Snapshot first, then tracker/group, matching JoinGame's post-B04 order.
    /// </summary>
    [SkippableFact]
    public async Task JoinAsSpectator_of_a_missing_game_does_not_count_the_connection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = pg.GetConnectionString(),
                    ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        var missingId = Guid.NewGuid();
        await using var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await conn.StartAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => conn.InvokeAsync("JoinAsSpectator", missingId.ToString()));
        Assert.Contains("Game not found.", ex.Message, StringComparison.Ordinal);

        var tracker = factory.Services.GetRequiredService<SpectatorTracker>();
        Assert.Equal(0, await tracker.CountForAsync(missingId));
    }

    /// <summary>
    /// C-2-9 = C-8-7. JoinAsSpectator withholds LegalMoves, then used to put the connection in the
    /// same group the players occupy, so the next MovePlayed handed the list over. Players still
    /// need the list. A spectator who also landed in the player group would receive two events;
    /// asserting a single empty payload is what catches that.
    /// </summary>
    [SkippableFact]
    public async Task A_spectator_MovePlayed_has_no_legal_moves_and_a_player_still_does()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await using var pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await pg.StartAsync();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = pg.GetConnectionString(),
                    ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        using var client = factory.CreateClient();

        async Task<AuthResult> RegisterAsync(string email, string displayName)
        {
            var reg = await client.PostAsJsonAsync("/api/auth/register", new
            {
                Email = email,
                Password = "P@ssword123!",
                DisplayName = displayName,
                AcceptedTerms = true,
                ConfirmedMinimumAge = true
            }, jsonOptions);
            reg.EnsureSuccessStatusCode();
            var auth = await reg.Content.ReadFromJsonAsync<AuthResult>(jsonOptions);
            Assert.NotNull(auth);
            return auth!;
        }

        var white = await RegisterAsync("spectator-legal-p1@example.com", "Legal White");
        var black = await RegisterAsync("spectator-legal-p2@example.com", "Legal Black");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var challengeResponse = await client.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "bounty1",
            Colour = "White"
        }, jsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(jsonOptions);
        Assert.NotNull(challenge);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", black.Token);
        var acceptResponse = await client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(jsonOptions);
        Assert.NotNull(accepted);
        var gameId = accepted!.GameId.ToString();

        await using var whiteConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(white.Token);
            })
            .Build();

        await using var spectatorConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var playerMoves = new List<MovePlayedDto>();
        var playerGate = new object();
        whiteConnection.On<MovePlayedDto>("MovePlayed", dto => { lock (playerGate) playerMoves.Add(dto); });

        var spectatorMoves = new List<MovePlayedDto>();
        var spectatorGate = new object();
        spectatorConnection.On<MovePlayedDto>("MovePlayed", dto => { lock (spectatorGate) spectatorMoves.Add(dto); });

        await whiteConnection.StartAsync();
        await spectatorConnection.StartAsync();

        var snapshot = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId);
        Assert.NotEmpty(snapshot.LegalMoves);

        var spectated = await spectatorConnection.InvokeAsync<SpectatorSnapshotDto>("JoinAsSpectator", gameId);
        Assert.NotNull(spectated);

        await whiteConnection.InvokeAsync("SubmitMove", gameId, snapshot.LegalMoves[0]);

        await WaitForCountAsync(playerMoves, playerGate, 1);
        await WaitForCountAsync(spectatorMoves, spectatorGate, 1);

        lock (playerGate)
        {
            Assert.Single(playerMoves);
            Assert.Equal(snapshot.LegalMoves[0], playerMoves[0].Uci);
            Assert.NotEmpty(playerMoves[0].LegalMoves);
        }

        lock (spectatorGate)
        {
            Assert.Single(spectatorMoves);
            Assert.Equal(snapshot.LegalMoves[0], spectatorMoves[0].Uci);
            Assert.Empty(spectatorMoves[0].LegalMoves);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        (await client.PostAsync($"/api/games/{gameId}/resign", content: null)).EnsureSuccessStatusCode();
    }

    private static async Task WaitForCountAsync(List<MovePlayedDto> events, object gate, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (gate)
            {
                if (events.Count >= count) return;
            }
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {count} MovePlayed event(s); saw {events.Count}.");
            await Task.Delay(20);
        }
    }

    // --- 8d: GET /api/games/live (Docker-gated) ---

    [SkippableFact]
    public async Task Live_games_endpoint_is_anonymous_lists_two_player_games_and_excludes_solo_games()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        using var client = factory.CreateClient();

        // Register three users. C-6-5 gave POST /api/games the already-in-a-game gate, so the solo
        // game below can no longer be created by a player who is sitting in the two-player game; it
        // gets its own account. What this test is about, the live list carrying two-player games and
        // excluding solo ones, is unchanged.
        var reg1 = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "spectator-p1@example.com",
            Password = "P@ssword123!",
            DisplayName = "Player One",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, jsonOptions);
        reg1.EnsureSuccessStatusCode();
        var auth1 = await reg1.Content.ReadFromJsonAsync<AuthResult>(jsonOptions);
        Assert.NotNull(auth1);

        var reg2 = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "spectator-p2@example.com",
            Password = "P@ssword123!",
            DisplayName = "Player Two",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, jsonOptions);
        reg2.EnsureSuccessStatusCode();
        var auth2 = await reg2.Content.ReadFromJsonAsync<AuthResult>(jsonOptions);
        Assert.NotNull(auth2);

        var reg3 = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "spectator-p3@example.com",
            Password = "P@ssword123!",
            DisplayName = "Player Three",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, jsonOptions);
        reg3.EnsureSuccessStatusCode();
        var auth3 = await reg3.Content.ReadFromJsonAsync<AuthResult>(jsonOptions);
        Assert.NotNull(auth3);

        // Phase 9d.8 closed POST /api/games' OpponentId hole (README scope decision 5): a
        // two-distinct-player game is now created the way a real player would, via a challenge. Colour
        // pinned to White for Player One so it still ends up White in the assertions below, matching
        // what the old direct-create path (caller always White) produced.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1!.Token);
        var challengeResponse = await client.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "bounty1",
            Colour = "White"
        }, jsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(jsonOptions);
        Assert.NotNull(challenge);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth2!.Token);
        var acceptResponse = await client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var acceptedTwoPlayer = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(jsonOptions);
        Assert.NotNull(acceptedTwoPlayer);
        var twoPlayerGameId = acceptedTwoPlayer!.GameId;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth3!.Token);

        var createSolo = await client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "bounty1",
            Rated = false
        }, jsonOptions);
        createSolo.EnsureSuccessStatusCode();
        var soloGame = await createSolo.Content.ReadFromJsonAsync<GameStateDto>(jsonOptions);
        Assert.NotNull(soloGame);

        // GET /api/games/live must be reachable with no Authorization header at all.
        using var anonymousClient = factory.CreateClient();
        var liveResponse = await anonymousClient.GetAsync("/api/games/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, liveResponse.StatusCode);

        var live = await liveResponse.Content.ReadFromJsonAsync<LiveGameRow[]>(jsonOptions);
        Assert.NotNull(live);

        var listedTwoPlayer = live!.SingleOrDefault(g => g.GameId == twoPlayerGameId);
        Assert.NotNull(listedTwoPlayer);
        Assert.Equal("Player One", listedTwoPlayer!.WhiteName);
        Assert.Equal("Player Two", listedTwoPlayer.BlackName);
        Assert.Equal(0, listedTwoPlayer.Spectators);
        Assert.Equal(0, listedTwoPlayer.MoveCount);

        Assert.DoesNotContain(live, g => g.GameId == soloGame!.GameId);

        // Regression: Step 4a touched hub auth, not endpoint auth. A direct game fetch must still 401.
        var directGameResponse = await anonymousClient.GetAsync($"/api/games/{twoPlayerGameId}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, directGameResponse.StatusCode);

        // Resign both games before teardown. LiveGamePersister.StopAsync flushes every still-live
        // session on host shutdown; leaving these ongoing would make the WebApplicationFactory
        // dispose race a snapshot write against the Postgres container's own shutdown, which is
        // unrelated to what this test is checking.
        // Each game is resigned by one of its own players. The solo game belongs to Player Three (see
        // the registration block above), so the header is already right for it; the two-player game
        // needs Player One back.
        (await client.PostAsync($"/api/games/{soloGame!.GameId}/resign", content: null)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth1.Token);
        (await client.PostAsync($"/api/games/{twoPlayerGameId}/resign", content: null)).EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Live_games_endpoint_ranks_by_combined_elo_ahead_of_spectators_and_move_count()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await using var pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        using var client = factory.CreateClient();

        async Task<AuthResult> RegisterAsync(string email, string displayName)
        {
            var reg = await client.PostAsJsonAsync("/api/auth/register", new
            {
                Email = email,
                Password = "P@ssword123!",
                DisplayName = displayName,
                AcceptedTerms = true,
                ConfirmedMinimumAge = true
            }, jsonOptions);
            reg.EnsureSuccessStatusCode();
            var auth = await reg.Content.ReadFromJsonAsync<AuthResult>(jsonOptions);
            Assert.NotNull(auth);
            return auth!;
        }

        // Two same-pool games, both with zero spectators and zero moves, so the pre-existing
        // spectators/move-count tiebreak cannot distinguish them. Only combined Elo can.
        var lowEloWhite = await RegisterAsync("combined-elo-p1@example.com", "Combined Low White");
        var lowEloBlack = await RegisterAsync("combined-elo-p2@example.com", "Combined Low Black");
        var highEloWhite = await RegisterAsync("combined-elo-p3@example.com", "Combined High White");
        var highEloBlack = await RegisterAsync("combined-elo-p4@example.com", "Combined High Black");

        // Both games must sit in a real rating pool or this test cannot observe what it claims to.
        // It used to create them by accepting a challenge, and that path is hardcoded to
        // `pool: null, rated: false` (ChallengeEndpoints.cs:209). LoadRatingsAsync contributes no
        // query key for a null pool, so RatingOf falls back to the default for every player and BOTH
        // games tie at 2 x DefaultRating no matter what the Ratings table below says.
        //
        // That tie is the whole of the "fails in streaks" behaviour recorded against this test.
        // With combined Elo, spectator count and move count all equal, LINQ's stable sort left the
        // order decided by the source enumeration, which is the registry's ConcurrentDictionary over
        // Guid keys. Its order is not stable from one process to the next, so the assertion below was
        // a coin flip weighted by whatever the hash layout happened to be that run.
        //
        // Creating the sessions straight through the registry with a pool is what gives the seeded
        // ratings something to attach to.
        //
        // B44 note: this made the test stop flaking, but it also means the test no longer proves
        // anything about a challenge-accepted game specifically. `ChallengeEndpoints.cs`'s accept
        // handler still calls `registry.CreateAsync(config, white, black, pool: null, rated: false)`
        // today, unchanged: `challenge.Pool` only selects which config preset to use and is then
        // dropped, so a real challenge game is always unpooled and unrated, and two such games
        // still tie on every key this test exercises and sort by ConcurrentDictionary enumeration
        // order on `/api/games/live`, exactly as this comment block used to describe. That is a
        // genuine open product question, not a test artifact: does a challenge-accepted game carry
        // its chosen pool and get rated, given the Glicko-farming risk between two friends that
        // motivated leaving it unrated in the first place (see the comment at
        // `ChallengeEndpoints.cs:37`)? Nothing in this suite currently exercises the real
        // accept-a-challenge path's position in the live-games ordering; this test only proves the
        // sort itself is correct once a game is properly pooled and rated. Do not read this test's
        // green result as evidence that a challenge game is ranked correctly on `/api/games/live`.
        var config = KiriChessConfig.CreateBounty1();
        var registry = factory.Services.GetRequiredService<GameRegistry>();
        var lowEloGameId = (await registry.CreateAsync(
            config, lowEloWhite.UserId, lowEloBlack.UserId, pool: "bounty1", rated: true)).Id;
        var highEloGameId = (await registry.CreateAsync(
            config, highEloWhite.UserId, highEloBlack.UserId, pool: "bounty1", rated: true)).Id;

        // Neither player has a Rating row yet, so both start at the default (1500 + 1500 = 3000).
        // Boost one side of the second game well past that so its combined Elo (2200 + 1500 = 3700)
        // must sort it ahead of the untouched game.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.Ratings.Add(new Rating { UserId = highEloWhite.UserId, Pool = "bounty1", Value = 2200.0 });
            await db.SaveChangesAsync();
        }

        using var anonymousClient = factory.CreateClient();
        var liveResponse = await anonymousClient.GetAsync("/api/games/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, liveResponse.StatusCode);

        var live = await liveResponse.Content.ReadFromJsonAsync<LiveGameRow[]>(jsonOptions);
        Assert.NotNull(live);

        var highEloIndex = Array.FindIndex(live!, g => g.GameId == highEloGameId);
        var lowEloIndex = Array.FindIndex(live, g => g.GameId == lowEloGameId);
        Assert.True(highEloIndex >= 0 && lowEloIndex >= 0);
        Assert.True(
            highEloIndex < lowEloIndex,
            "Higher combined-Elo game must rank ahead of a tied-spectator, tied-move-count game with lower combined Elo.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", lowEloWhite.Token);
        (await client.PostAsync($"/api/games/{lowEloGameId}/resign", content: null)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", highEloWhite.Token);
        (await client.PostAsync($"/api/games/{highEloGameId}/resign", content: null)).EnsureSuccessStatusCode();
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

    private sealed record LiveGameRow(
        Guid GameId,
        string WhiteName,
        string BlackName,
        string? Pool,
        bool Rated,
        int WhiteTime,
        int BlackTime,
        string SideToMove,
        int MoveCount,
        int Spectators,
        DateTimeOffset StartedAt);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record ChallengeAcceptResultDto(Guid GameId);
}
