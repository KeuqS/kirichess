using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Games;
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
/// C-6-1 and C-6-5. Two rules that only hold together: a player standing in a matchmaking queue must
/// stop standing in it the moment they enter a game by any route, and a pairing must be re-checked
/// against the player who was already waiting, not just against the entrant.
///
/// Fixture scaffolding copied from <see cref="RematchTests"/>; the hub connection recipe from
/// <see cref="HubGameGroupTests"/>. Requires Docker; skips cleanly without it.
/// </summary>
public sealed class QueueLeaveOnGameCreationTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "queue-leave-test-signing-key-at-least-32-bytes",
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

    // ---- C-6-1, first half: the waiting player is re-checked before the game is created ----

    /// <summary>
    /// The exact state the finding describes: a player is queued, then enters a game by some other
    /// route while their queue entry stands. The next stranger to join that pool must not be paired
    /// against them. Before the fix, TryPairAsync's return value went straight to CreateAsync and the
    /// stranger got a game that sits Ongoing until MatchNoShowWatcher aborts it 45 seconds later.
    /// </summary>
    [SkippableFact]
    public async Task A_waiting_player_who_is_already_in_a_game_is_not_paired()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var waiter = await RegisterAsync("queue-recheck-waiter@example.com", "RecheckWaiter");
        var stranger = await RegisterAsync("queue-recheck-stranger@example.com", "RecheckStranger");
        var third = await RegisterAsync("queue-recheck-third@example.com", "RecheckThird");

        var matchmaking = _factory!.Services.GetRequiredService<MatchmakingService>();
        var registry = _factory.Services.GetRequiredService<GameRegistry>();

        Assert.Null(await matchmaking.TryJoinAsync(waiter.UserId, "conn-waiter", "steal1"));

        // Straight through the registry, which is what all three of the creation paths the finding
        // names ultimately do. The queue entry above is untouched by this, which is the whole bug.
        var elsewhere = await registry.CreateAsync(
            KiriChessConfig.Presets["steal1"].Clone(), waiter.UserId, third.UserId, "steal1", rated: true);
        Assert.Contains(waiter.UserId, registry.PlayersInGame);

        var match = await matchmaking.TryJoinAsync(stranger.UserId, "conn-stranger", "steal1");

        Assert.Null(match);
        Assert.DoesNotContain(
            registry.ActiveSessions,
            s => s.Id != elsewhere.Id
                 && (s.WhitePlayerId == stranger.UserId || s.BlackPlayerId == stranger.UserId));

        // And the innocent entrant is not stranded. The stale entry was discarded and the entrant took
        // its place, so exactly one person is waiting in the pool and it is the stranger.
        Assert.Equal(1, matchmaking.QueueDepths["steal1"]);
    }

    // ---- C-6-1, second half: every creating path leaves the queue ----

    [SkippableFact]
    public async Task Creating_a_game_over_http_leaves_the_queue()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var player = await RegisterAsync("queue-leave-http@example.com", "QueueLeaveHttp");
        var matchmaking = _factory!.Services.GetRequiredService<MatchmakingService>();

        Assert.Null(await matchmaking.TryJoinAsync(player.UserId, "conn-http", "burn2"));
        Assert.Equal(1, matchmaking.QueueDepths["burn2"]);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        var created = await _client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "burn2",
            Rated = false
        }, JsonOptions);
        created.EnsureSuccessStatusCode();

        Assert.Equal(0, matchmaking.QueueDepths["burn2"]);
    }

    [SkippableFact]
    public async Task Accepting_a_challenge_leaves_the_queue_for_both_players()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("queue-leave-challenger@example.com", "QueueLeaveChal");
        var friend = await RegisterAsync("queue-leave-friend@example.com", "QueueLeaveFriend");
        var matchmaking = _factory!.Services.GetRequiredService<MatchmakingService>();

        // Different pools on purpose: two people waiting in the same pool would simply pair.
        Assert.Null(await matchmaking.TryJoinAsync(challenger.UserId, "conn-chal", "steal1"));
        Assert.Null(await matchmaking.TryJoinAsync(friend.UserId, "conn-friend", "burn2"));
        Assert.Equal(1, matchmaking.QueueDepths["steal1"]);
        Assert.Equal(1, matchmaking.QueueDepths["burn2"]);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenger.Token);
        var createResponse = await _client.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "steal1",
            Colour = "Random"
        }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var challenge = await createResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", friend.Token);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();

        Assert.Equal(0, matchmaking.QueueDepths["steal1"]);
        Assert.Equal(0, matchmaking.QueueDepths["burn2"]);
    }

    [SkippableFact]
    public async Task Accepting_a_rematch_leaves_the_queue_for_both_players()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        var matchmaking = _factory!.Services.GetRequiredService<MatchmakingService>();

        await using var whiteHub = BuildGameHubConnection(white.Token);
        await using var blackHub = BuildGameHubConnection(black.Token);
        await whiteHub.StartAsync();
        await blackHub.StartAsync();

        await whiteHub.InvokeAsync("OfferRematch", gameId.ToString());

        // Queued only after the offer, so nothing here is a leftover from playing the first game.
        Assert.Null(await matchmaking.TryJoinAsync(white.UserId, "conn-rematch-white", "steal1"));
        Assert.Null(await matchmaking.TryJoinAsync(black.UserId, "conn-rematch-black", "burn2"));
        Assert.Equal(1, matchmaking.QueueDepths["steal1"]);
        Assert.Equal(1, matchmaking.QueueDepths["burn2"]);

        await blackHub.InvokeAsync("AcceptRematch", gameId.ToString());

        Assert.Equal(0, matchmaking.QueueDepths["steal1"]);
        Assert.Equal(0, matchmaking.QueueDepths["burn2"]);
    }

    // ---- C-6-5 ----

    /// <summary>
    /// A solo game is one account on both sides. The row used to say IsRated = true and
    /// RatingPool = steal1 anyway, and only RatingService's own WhitePlayerId == BlackPlayerId guard,
    /// two layers later, kept a rating from moving. The metrics counter and the pool-balance query
    /// read the row rather than RatingService, so the row is what has to be honest.
    /// </summary>
    [SkippableFact]
    public async Task A_solo_game_is_never_persisted_as_rated_or_pooled()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var player = await RegisterAsync("solo-unrated@example.com", "SoloUnrated");
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);

        var response = await _client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "steal1",
            Rated = true // asked for rated, in an official pool, with both seats the same account
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<GameIdDto>(JsonOptions);
        Assert.NotNull(created);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Games.SingleAsync(g => g.Id == created!.GameId);

        Assert.Equal(row.WhitePlayerId, row.BlackPlayerId);
        Assert.False(row.IsRated);
        Assert.Null(row.RatingPool);
    }

    /// <summary>
    /// The eligibility gate the endpoint never had: it checked only SanctionScope.Account, so it was
    /// the one game-creating path that would happily open a second, third and hundredth live game for
    /// a player already sitting in one.
    /// </summary>
    [SkippableFact]
    public async Task Creating_a_game_is_refused_while_already_in_a_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var player = await RegisterAsync("solo-already-in-game@example.com", "SoloAlready");
        var opponent = await RegisterAsync("solo-already-opponent@example.com", "SoloAlreadyOpp");

        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        await registry.CreateAsync(
            KiriChessConfig.Presets["steal1"].Clone(), player.UserId, opponent.UserId, "steal1", rated: true);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        var response = await _client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "burn2",
            Rated = false
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- helpers ----

    /// <summary>Registers two players, plays a rated game to a resignation, and returns (white, black, gameId).</summary>
    private async Task<(AuthResult White, AuthResult Black, Guid GameId)> FinishRatedGameAsync(
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var white = await RegisterAsync($"queue-leave-{testName}-white@example.com", "QueueLeaveWhite");
        var black = await RegisterAsync($"queue-leave-{testName}-black@example.com", "QueueLeaveBlack");

        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var session = await registry.CreateAsync(
            KiriChessConfig.Presets["steal1"].Clone(), white.UserId, black.UserId, "steal1", rated: true);
        var gameId = session.Id;

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();

        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        _client.DefaultRequestHeaders.Authorization = null;
        return (white, black, gameId);
    }

    private HubConnection BuildGameHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                // TestServer has no real socket, so long polling over the factory's own handler is
                // what lets a HubConnection reach this host at all.
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var previous = _client!.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var response = await _client.PostAsJsonAsync("/api/auth/register", new
            {
                Email = email,
                Password,
                DisplayName = displayName,
                AcceptedTerms = true,
                ConfirmedMinimumAge = true
            }, JsonOptions);
            response.EnsureSuccessStatusCode();
            var auth = await response.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
            Assert.NotNull(auth);
            return auth!;
        }
        finally
        {
            _client.DefaultRequestHeaders.Authorization = previous;
        }
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record GameIdDto(Guid GameId);
}
