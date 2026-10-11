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
/// C-1-2 = C-11-2. <c>GameSnapshotDto</c> used to be state plus legal moves, while
/// <c>SpectatorSnapshotDto</c> carried a whole move log, so <c>JoinGame</c> handed a rejoining
/// player nothing to rebuild from: every ply broadcast while their transport was down stayed missing
/// from their move list, clock graph and captured-material panel for the rest of the game, and every
/// later ply number was off by that count. A spectator watching the same board kept all three.
///
/// So the claim these tests pin is deliberately comparative, not just "the log is non-empty": a
/// participant's snapshot must carry the same move log a spectator's does, on the live path and on
/// the finished-game path both. The board and clocks were always right, which is exactly what made
/// the divergence invisible from the outside, so a test is the only place it shows.
///
/// One host, no Redis: the snapshot is built by <c>GameActionExecutor.JoinGameLocalAsync</c> and
/// <c>FinishedGameReader</c>, neither of which is cluster-dependent. Requires Docker; skips cleanly
/// without it.
/// </summary>
public sealed class GameSnapshotMoveLogTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "snapshot-move-log-test-signing-key-at-least-32-bytes",
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
    /// The defect itself. A player's connection drops after two plies and comes back on a fresh
    /// connection, which is what <c>onreconnected</c> amounts to from the server's side: the second
    /// <c>JoinGame</c> has to describe the whole game, not just the position.
    /// </summary>
    [SkippableFact]
    public async Task JoinGame_returns_the_move_log_a_rejoining_participant_rebuilds_from()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("snapshot-log-white@example.com", "Snapshot White");
        var black = await RegisterAsync("snapshot-log-black@example.com", "Snapshot Black");
        var watcher = await RegisterAsync("snapshot-log-viewer@example.com", "Snapshot Viewer");
        var gameId = await CreateGameAsync(white, black);

        await using var whiteConnection = BuildGameHubConnection(white.Token);
        await using var blackConnection = BuildGameHubConnection(black.Token);
        await whiteConnection.StartAsync();
        await blackConnection.StartAsync();

        // Ply 0. An empty log, not a null one: the client maps over it unconditionally.
        var opening = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        Assert.Empty(opening.Moves);
        await whiteConnection.InvokeAsync("SubmitMove", gameId.ToString(), opening.LegalMoves[0]);

        // Black joins after White has already moved, the "arrived at ply 30" case in miniature.
        var afterWhite = await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        Assert.Single(afterWhite.Moves);
        Assert.Equal(opening.LegalMoves[0], afterWhite.Moves[0].Uci);
        await blackConnection.InvokeAsync("SubmitMove", gameId.ToString(), afterWhite.LegalMoves[0]);

        // The reconnect. A new connection for the same account is what the client's transport blip
        // produces, and it must not receive less than the connection it replaced held.
        await using var whiteReconnect = BuildGameHubConnection(white.Token);
        await whiteReconnect.StartAsync();
        var resumed = await whiteReconnect.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        Assert.Equal(2, resumed.Moves.Count);
        Assert.Equal([1, 2], resumed.Moves.Select(m => m.Ply));
        Assert.Equal(["White", "Black"], resumed.Moves.Select(m => m.Mover));
        Assert.Equal(opening.LegalMoves[0], resumed.Moves[0].Uci);
        Assert.Equal(afterWhite.LegalMoves[0], resumed.Moves[1].Uci);
        // The clock graph is rebuilt off these two columns; a gap in them is a gap in the graph.
        Assert.All(resumed.Moves, m => Assert.True(m.WhiteTimeAfter > 0 && m.BlackTimeAfter > 0));

        // The comparison the finding is actually about: an anonymous spectator of the same game must
        // not be better informed than one of its players.
        await using var watcherConnection = BuildGameHubConnection(watcher.Token);
        await watcherConnection.StartAsync();
        var spectated = await watcherConnection.InvokeAsync<SpectatorSnapshotDto>("JoinAsSpectator", gameId.ToString());
        Assert.Equal(spectated.Moves, resumed.Moves);
    }

    /// <summary>
    /// The path a socket blip lands on when it happens just after the game ended:
    /// <c>PersistIfOverAsync</c> has evicted the session, so <c>JoinGame</c> falls through to
    /// <see cref="FinishedGameReader"/>. That branch has to carry the log too. It is the client's
    /// rebuild being unconditional that makes this load-bearing rather than cosmetic: an empty log
    /// here would blank the move list and the game-over clock graph on the last reconnect of the
    /// game, which is the one every player sees.
    /// </summary>
    [SkippableFact]
    public async Task JoinGame_returns_the_move_log_for_a_game_that_has_already_ended()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("snapshot-ended-white@example.com", "Ended White");
        var black = await RegisterAsync("snapshot-ended-black@example.com", "Ended Black");
        var gameId = await CreateGameAsync(white, black);

        await using var whiteConnection = BuildGameHubConnection(white.Token);
        await whiteConnection.StartAsync();

        var opening = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        await whiteConnection.InvokeAsync("SubmitMove", gameId.ToString(), opening.LegalMoves[0]);
        await whiteConnection.InvokeAsync("Resign", gameId.ToString());

        await using var whiteReconnect = BuildGameHubConnection(white.Token);
        await whiteReconnect.StartAsync();
        var resumed = await whiteReconnect.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        Assert.NotEqual("Ongoing", resumed.State.Status);
        Assert.Empty(resumed.LegalMoves);
        Assert.Single(resumed.Moves);
        Assert.Equal(opening.LegalMoves[0], resumed.Moves[0].Uci);
        Assert.Equal("White", resumed.Moves[0].Mover);
    }

    /// <summary>
    /// The same two-distinct-player setup <see cref="HubGameGroupTests"/> uses: a challenge link
    /// created by one account and accepted by the other, since 9d.8 closed <c>POST /api/games</c>'s
    /// OpponentId hole.
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
                // TestServer has no real socket, so long polling over the factory's own handler is
                // what lets a HubConnection reach this host at all.
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
