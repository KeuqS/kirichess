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
/// Who ends up in a game's SignalR group, and under what name. Both claims here are about the two
/// lines of <see cref="GameHub.JoinGame"/> that decide it, and both were bugs:
///
/// C-2-6 / C-5-5 / S-1-9: the group join used to run <i>before</i> the authorization inside
/// <c>JoinGameAsync</c>, and nothing removed the connection again when that threw. Any authenticated
/// caller naming any game id therefore received that game's whole event stream for the life of the
/// connection - an uncounted watcher the Spectating kill switch could not turn off, that
/// <see cref="SpectatorTracker"/> never saw, and that 10a.2's anonymous-connection reserve was never
/// sized for.
///
/// C-5-4: the group was named by the raw client string while every broadcast site names it
/// <c>session.Id.ToString()</c>. <c>Guid.TryParse</c> accepts the "N", "B", "P" and "X" formats and
/// any casing, so a caller joining with a dashless or braced id got a correct snapshot and then
/// nothing at all - invisible from the first-party UI, whose route params are already canonical, and
/// squarely on the third-party bot authors <c>/developers</c> is trying to attract.
///
/// One host, no Redis: both claims are about local group membership, which is the part of the hub
/// that stays in-process whatever the cluster looks like. Requires Docker; skips cleanly without it.
/// </summary>
public sealed class HubGameGroupTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "hub-group-test-signing-key-at-least-32-bytes",
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
    /// S-1-9's required test. A third account that is in neither seat calls <c>JoinGame</c>, is
    /// refused, and must then receive nothing when the players actually play.
    /// </summary>
    [SkippableFact]
    public async Task A_non_participant_who_calls_JoinGame_receives_no_MovePlayed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("hub-group-intruder-white@example.com", "Group White");
        var black = await RegisterAsync("hub-group-intruder-black@example.com", "Group Black");
        var intruder = await RegisterAsync("hub-group-intruder-third@example.com", "Group Bystander");
        var gameId = await CreateGameAsync(white, black);

        await using var whiteConnection = BuildGameHubConnection(white.Token);
        await using var intruderConnection = BuildGameHubConnection(intruder.Token);

        var whiteMoves = new List<MovePlayedDto>();
        var whiteGate = new object();
        whiteConnection.On<MovePlayedDto>("MovePlayed", dto => { lock (whiteGate) whiteMoves.Add(dto); });

        var intruderMoves = new List<MovePlayedDto>();
        var intruderGate = new object();
        intruderConnection.On<MovePlayedDto>("MovePlayed", dto => { lock (intruderGate) intruderMoves.Add(dto); });

        await whiteConnection.StartAsync();
        await intruderConnection.StartAsync();

        // The refusal itself: the domain layer has always rejected this caller. What the bug was
        // about is what the hub did to the connection on the way to that rejection.
        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => intruderConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString()));
        Assert.Contains("Not a player in this game.", refused.Message, StringComparison.Ordinal);

        var snapshot = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        Assert.NotEmpty(snapshot.LegalMoves);
        await whiteConnection.InvokeAsync("SubmitMove", gameId.ToString(), snapshot.LegalMoves[0]);

        // White is in the group, so its MovePlayed arriving means the broadcast has been made and
        // fanned out. Anything queued for the intruder's connection was queued by the same call.
        await WaitForCountAsync(whiteMoves, whiteGate, 1);

        // A round trip on the intruder's own connection, after that: with long polling the reply to
        // an invocation travels the same server-to-client channel a broadcast would, so a MovePlayed
        // queued for this connection would have been delivered by the time this returns. LeaveQueue
        // is the cheapest authorized method that touches nothing.
        await intruderConnection.InvokeAsync("LeaveQueue");

        lock (intruderGate)
            Assert.Empty(intruderMoves);
    }

    /// <summary>
    /// C-5-4. The "N" format is the sharp end of it: <c>Guid.TryParse</c> takes it, so the caller gets
    /// a correct snapshot and believes it is subscribed, and then its clock runs down in silence.
    /// </summary>
    [SkippableTheory]
    [InlineData("N")]
    [InlineData("B")]
    public async Task A_player_joining_with_a_non_canonical_game_id_still_receives_MovePlayed(string format)
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var suffix = format.ToLowerInvariant();
        var white = await RegisterAsync($"hub-group-format-{suffix}-white@example.com", $"Format White {format}");
        var black = await RegisterAsync($"hub-group-format-{suffix}-black@example.com", $"Format Black {format}");
        var gameId = await CreateGameAsync(white, black);

        // Uppercased as well as reformatted: casing is the other half of what TryParse accepts and
        // ToString() never emits.
        var oddId = gameId.ToString(format).ToUpperInvariant();
        Assert.NotEqual(gameId.ToString(), oddId);

        await using var whiteConnection = BuildGameHubConnection(white.Token);
        await using var blackConnection = BuildGameHubConnection(black.Token);

        var whiteMoves = new List<MovePlayedDto>();
        var whiteGate = new object();
        whiteConnection.On<MovePlayedDto>("MovePlayed", dto => { lock (whiteGate) whiteMoves.Add(dto); });

        await whiteConnection.StartAsync();
        await blackConnection.StartAsync();

        // White is the one using the odd spelling; Black joins canonically and is the mover, so the
        // broadcast White must receive is one it did not make itself.
        var whiteSnapshot = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", oddId);
        Assert.NotEmpty(whiteSnapshot.LegalMoves);
        await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        // White moves first under standard rules, so White's own move is what puts a MovePlayed on
        // the wire; submitted through the odd spelling too, since ParseGameId is what routes it.
        await whiteConnection.InvokeAsync("SubmitMove", oddId, whiteSnapshot.LegalMoves[0]);

        await WaitForCountAsync(whiteMoves, whiteGate, 1);
        Assert.Equal(whiteSnapshot.LegalMoves[0], whiteMoves[0].Uci);
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

    /// <summary>
    /// A two-distinct-player game the way a real pair of players makes one (9d.8 closed
    /// <c>POST /api/games</c>'s OpponentId hole): the challenger creates a link, the opponent accepts.
    /// </summary>
    private async Task<Guid> CreateGameAsync(AuthResult white, AuthResult black)
    {
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var created = await _client.PostAsJsonAsync("/api/challenges", new { Pool = "bounty1", Colour = "White" }, JsonOptions);
        created.EnsureSuccessStatusCode();
        var challenge = await created.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", black.Token);
        var accepted = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        accepted.EnsureSuccessStatusCode();
        var result = await accepted.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(result);

        _client.DefaultRequestHeaders.Authorization = null;
        return result!.GameId;
    }

    private HubConnection BuildGameHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                // Same recipe as TwoInstanceGameTests and KillSwitchTests: TestServer has no real
                // socket, so long polling over the factory's own handler is what lets a
                // HubConnection reach this host at all.
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

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

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record ChallengeAcceptResultDto(Guid GameId);
}
