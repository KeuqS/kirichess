using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// C-2-2 and S-1-15: there used to be three implementations of "apply a move" and only one of them
/// told anybody. <c>GameHub</c> went through <see cref="IGameActionExecutor"/>, which broadcasts
/// <c>MovePlayed</c> and routes to the instance that owns the game;
/// <c>POST /api/games/{id}/moves</c> and <c>POST /api/v1/games/{id}/moves</c> each carried their own
/// copy of the body, reached into <c>GameRegistry</c> directly, and sent nothing. A bot playing a
/// human over the documented <c>/api/v1</c> surface therefore moved into silence: the human's board
/// sat on the previous position until their clock ran out.
///
/// These tests are the ones that would have caught it, so they assert the thing the duplication hid
/// rather than the code that replaced it. The opponent is a real SignalR connection in the game's
/// group, and the move and the resignation arrive over HTTP from somebody who never joined the hub
/// at all. If a future change re-inlines either handler, the state assertions still pass and these
/// fail, which is the whole point.
///
/// One host, no Redis. The ownership half of S-1-15 is covered by <c>TwoInstanceGameTests</c> and
/// <c>GameCommandRoutingTests</c> against the executor these handlers now share; what is new here is
/// that the HTTP handlers reach that executor at all. Requires Docker; skips cleanly without it.
/// </summary>
public sealed class HttpPlayBroadcastTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "http-play-broadcast-test-signing-key-32b",
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

    /// <summary>
    /// The first-party HTTP move path. White never opens a hub connection; Black is watching the
    /// board the way the real client does, and must be told the move happened.
    /// </summary>
    [SkippableFact]
    public async Task A_move_posted_to_api_games_reaches_the_opponents_hub_connection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("http-bcast-move-white@example.com", "Bcast Move White");
        var black = await RegisterAsync("http-bcast-move-black@example.com", "Bcast Move Black");
        var gameId = await CreateGameAsync(white, black);

        await using var blackConnection = BuildGameHubConnection(black.Token);
        var moves = Collect<MovePlayedDto>(blackConnection, "MovePlayed", out var gate);
        await blackConnection.StartAsync();
        await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        AuthorizeJwt(white);
        var response = await _client!.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "e2e4" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForCountAsync(moves, gate, 1);
        lock (gate)
        {
            Assert.Equal("e2e4", moves[0].Uci);
            Assert.Equal(1, moves[0].State.MoveCount);
            // The push has to be usable, not merely present: without the legal-move list the client
            // has nothing to offer the player whose turn it now is.
            Assert.NotEmpty(moves[0].LegalMoves);
        }

        // The response body is still the one a client already parses: MoveResponse, not the hub DTO.
        var body = await response.Content.ReadFromJsonAsync<MoveResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(1, body!.State.MoveCount);
        Assert.False(body.GameOver);
    }

    /// <summary>
    /// The same for the published bot surface, which is the sharp end of C-2-2: <c>/developers</c>
    /// advertises <c>/api/v1/games/{id}/moves</c> as the way to write a bot, and a bot that cannot
    /// tell its human opponent it moved is not a play surface.
    /// </summary>
    [SkippableFact]
    public async Task A_move_posted_to_the_public_bot_api_reaches_the_opponents_hub_connection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("http-bcast-v1-white@example.com", "Bcast V1 White");
        var black = await RegisterAsync("http-bcast-v1-black@example.com", "Bcast V1 Black");
        var gameId = await CreateGameAsync(white, black);

        AuthorizeJwt(white);
        var botToken = await CreateTokenAsync("bcast bot", ["play"]);

        await using var blackConnection = BuildGameHubConnection(black.Token);
        var moves = Collect<MovePlayedDto>(blackConnection, "MovePlayed", out var gate);
        await blackConnection.StartAsync();
        await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        AuthorizeToken(botToken);
        var response = await _client!.PostAsJsonAsync($"/api/v1/games/{gameId}/moves", new { Uci = "e2e4" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForCountAsync(moves, gate, 1);
        lock (gate) Assert.Equal("e2e4", moves[0].Uci);
    }

    /// <summary>
    /// Resignation, the other half of the same finding. A bot that gives up over HTTP used to leave
    /// its opponent looking at a live board of a game that had already been written to the database
    /// as a loss.
    /// </summary>
    [SkippableFact]
    public async Task A_resignation_posted_over_http_reaches_the_opponents_hub_connection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("http-bcast-resign-white@example.com", "Bcast Resign White");
        var black = await RegisterAsync("http-bcast-resign-black@example.com", "Bcast Resign Black");
        var gameId = await CreateGameAsync(white, black);

        await using var blackConnection = BuildGameHubConnection(black.Token);
        var ended = Collect<GameEndedDto>(blackConnection, "GameEnded", out var gate);
        await blackConnection.StartAsync();
        await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        AuthorizeJwt(white);
        var response = await _client!.PostAsync($"/api/games/{gameId}/resign", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForCountAsync(ended, gate, 1);
        lock (gate)
        {
            Assert.Equal("Black", ended[0].State.Winner);
            Assert.Equal("Resignation", ended[0].State.Termination);
        }

        var body = await response.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("Black", body!.Winner);
    }

    /// <summary>
    /// The regression guard the finding is really about: three surfaces, one answer. Three games from
    /// the same preset, one identical opening move through the hub, through <c>/api/games</c> and
    /// through <c>/api/v1/games</c>, compared on everything the engine decides. The clocks are
    /// excluded on purpose - they are charged from real elapsed think time, so comparing them would
    /// test the test runner's speed, not the three code paths.
    ///
    /// A pair of accounts per surface, not one pair playing three games: a player already in a game
    /// cannot accept a second challenge, which is a rule of the product rather than anything this
    /// test wants to work around.
    /// </summary>
    [SkippableFact]
    public async Task The_hub_and_both_http_surfaces_produce_the_same_state_for_the_same_move()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var hubWhite = await RegisterAsync("http-agree-hub-w@example.com", "Agree Hub White");
        var hubBlack = await RegisterAsync("http-agree-hub-b@example.com", "Agree Hub Black");
        var restWhite = await RegisterAsync("http-agree-rest-w@example.com", "Agree Rest White");
        var restBlack = await RegisterAsync("http-agree-rest-b@example.com", "Agree Rest Black");
        var v1White = await RegisterAsync("http-agree-v1-w@example.com", "Agree V1 White");
        var v1Black = await RegisterAsync("http-agree-v1-b@example.com", "Agree V1 Black");

        var hubGame = await CreateGameAsync(hubWhite, hubBlack);
        var restGame = await CreateGameAsync(restWhite, restBlack);
        var v1Game = await CreateGameAsync(v1White, v1Black);

        AuthorizeJwt(v1White);
        var botToken = await CreateTokenAsync("agree bot", ["play"]);

        // Surface 1: the hub. White reads its own broadcast back, which is the only place the hub
        // path exposes a resulting state at all.
        await using var whiteConnection = BuildGameHubConnection(hubWhite.Token);
        var moves = Collect<MovePlayedDto>(whiteConnection, "MovePlayed", out var gate);
        await whiteConnection.StartAsync();
        await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", hubGame.ToString());
        await whiteConnection.InvokeAsync("SubmitMove", hubGame.ToString(), "e2e4");
        await WaitForCountAsync(moves, gate, 1);
        MovePlayedDto viaHub;
        lock (gate) viaHub = moves[0];

        // Surface 2: the first-party HTTP route.
        AuthorizeJwt(restWhite);
        var restResponse = await _client!.PostAsJsonAsync($"/api/games/{restGame}/moves", new { Uci = "e2e4" }, JsonOptions);
        restResponse.EnsureSuccessStatusCode();
        var viaRest = await restResponse.Content.ReadFromJsonAsync<MoveResponse>(JsonOptions);
        Assert.NotNull(viaRest);

        // Surface 3: the published bot route.
        AuthorizeToken(botToken);
        var v1Response = await _client.PostAsJsonAsync($"/api/v1/games/{v1Game}/moves", new { Uci = "e2e4" }, JsonOptions);
        v1Response.EnsureSuccessStatusCode();
        var viaV1 = await v1Response.Content.ReadFromJsonAsync<MoveResponse>(JsonOptions);
        Assert.NotNull(viaV1);

        Assert.Equal(Fingerprint(viaHub.State), Fingerprint(viaRest!.State));
        Assert.Equal(Fingerprint(viaHub.State), Fingerprint(viaV1!.State));

        // The clock economy is config-derived, not clock-derived, so the event list is comparable
        // even though the times are not. This is the part that silently drifted before.
        Assert.Equal(Economy(viaHub.Events), Economy(viaRest.Events));
        Assert.Equal(Economy(viaHub.Events), Economy(viaV1.Events));

        Assert.Equal(viaHub.GameOver, viaRest.GameOver);
        Assert.Equal(viaHub.GameOver, viaV1.GameOver);
    }

    /// <summary>
    /// The error contract, which routing through the executor must not have moved. Every one of these
    /// is a status and a body a bot written against the old handlers already depends on, and the
    /// executor reports all of them as one exception type carrying one message string.
    /// </summary>
    [SkippableFact]
    public async Task Rejected_moves_and_resignations_keep_their_status_codes_and_bodies()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("http-reject-white@example.com", "Reject White");
        var black = await RegisterAsync("http-reject-black@example.com", "Reject Black");
        var stranger = await RegisterAsync("http-reject-third@example.com", "Reject Bystander");
        var gameId = await CreateGameAsync(white, black);

        // A game nobody has ever created.
        AuthorizeJwt(white);
        var missing = await _client!.PostAsJsonAsync($"/api/games/{Guid.NewGuid()}/moves", new { Uci = "e2e4" }, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Not the player on turn: Black at move one, and a third account in neither seat. Both are
        // 403 with no body, and neither is allowed to become a 400 that a bot would retry.
        AuthorizeJwt(black);
        var offTurn = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "e7e5" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, offTurn.StatusCode);

        AuthorizeJwt(stranger);
        var outsider = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "e2e4" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);

        // An unparseable UCI and a legally impossible one, both 400 carrying the engine's wording.
        AuthorizeJwt(white);
        var garbage = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "not-a-move" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorOf(garbage)));

        var illegal = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "e2e5" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, illegal.StatusCode);
        Assert.Equal("Illegal move 'e2e5'.", await ErrorOf(illegal));

        // A stranger resigning somebody else's game is 400, not 403: the resign handler never had a
        // participation pre-check, so the session's own message is what it has always returned.
        AuthorizeJwt(stranger);
        var notMine = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);
        Assert.Equal("Not a player in this game.", await ErrorOf(notMine));

        // Finish the game, then re-submit into it. "Game is over." is the move endpoint's own
        // historical wording, deliberately kept although the session says "Game is already over.".
        AuthorizeJwt(white);
        (await _client.PostAsync($"/api/games/{gameId}/resign", content: null)).EnsureSuccessStatusCode();

        var afterEnd = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = "e2e4" }, JsonOptions);
        // A finished game leaves the registry the moment it persists (P-3-1a), so this is 404 rather
        // than the 400 a still-resident finished session would answer. Either way it is not a 500 and
        // not a silently applied move.
        Assert.True(
            afterEnd.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"Expected 404 or 400 after the game ended, got {(int)afterEnd.StatusCode}.");
    }

    // ----------------------------------------------------------------------------------------------

    /// <summary>Everything the engine decides about the resulting position, and nothing the wall clock does.</summary>
    private static string Fingerprint(GameStateResponse s) =>
        string.Join('|', s.Fen, s.SideToMove, s.Status, s.Winner ?? "-", s.Termination, s.MoveCount, s.Rated);

    private static string Economy(IEnumerable<ClockEventDto> events) =>
        string.Join('|', events.Select(e => $"{e.Type}:{e.Side}:{e.Amount}"));

    private async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions);
        return body?.Error;
    }

    private static List<T> Collect<T>(HubConnection connection, string eventName, out object gate)
    {
        var received = new List<T>();
        var localGate = new object();
        gate = localGate;
        connection.On<T>(eventName, item => { lock (localGate) received.Add(item); });
        return received;
    }

    private static async Task WaitForCountAsync<T>(List<T> received, object gate, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (gate)
            {
                if (received.Count >= count) return;
            }
            if (DateTime.UtcNow > deadline)
            {
                int seen;
                lock (gate) seen = received.Count;
                Assert.Fail($"Timed out waiting for {count} {typeof(T).Name} event(s); saw {seen}.");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A game between two distinct players, made the way a real pair makes one: 9d.8 closed
    /// <c>POST /api/games</c>'s OpponentId hole, so a challenge link is the only route.
    /// </summary>
    private async Task<Guid> CreateGameAsync(AuthResult white, AuthResult black)
    {
        AuthorizeJwt(white);
        var created = await _client!.PostAsJsonAsync("/api/challenges", new { Pool = "bounty1", Colour = "White" }, JsonOptions);
        created.EnsureSuccessStatusCode();
        var challenge = await created.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        AuthorizeJwt(black);
        var accepted = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        accepted.EnsureSuccessStatusCode();
        var result = await accepted.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(result);

        _client.DefaultRequestHeaders.Authorization = null;
        return result!.GameId;
    }

    private async Task<string> CreateTokenAsync(string name, string[] scopes)
    {
        var response = await _client!.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = name, Scopes = scopes, Password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CreatedApiTokenDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!.Token;
    }

    private HubConnection BuildGameHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                // TestServer has no real socket, so long polling over the factory's own handler is
                // what lets a HubConnection reach this host at all (same recipe as HubGameGroupTests).
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private void AuthorizeJwt(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private void AuthorizeToken(string raw) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
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

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record CreatedApiTokenDto(Guid Id, string Name, string Token);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record ChallengeAcceptResultDto(Guid GameId);

    private sealed record ErrorBody(string? Error);
}
