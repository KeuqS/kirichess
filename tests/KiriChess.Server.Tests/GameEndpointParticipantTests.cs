using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// C-8-5. <c>GET /api/games/{id}</c> and <c>GET /api/games/{id}/legal-moves</c> had no participant
/// check while their <c>/api/v1</c> twins in <c>PublicApiEndpoints</c> did, so the bot API was
/// strictly stricter than the first-party API for the same data, and any authenticated account could
/// pull the engine's move-generator output for a live game it was not in. That is the same output
/// <c>SpectatorSnapshotDto</c> deliberately withholds.
///
/// These tests pin the answer a non-participant gets, and pin that it is the twin's answer: 404, not
/// 403. A 403 would confirm that the id names a real live game, which is the fact being withheld.
///
/// Nothing here touches the spectator surface. Watching is anonymous by design and that half of the
/// exposure (C-2-9 = C-8-7) is a separate piece of work.
///
/// Requires Docker; skips cleanly when no daemon is reachable.
/// </summary>
public sealed class GameEndpointParticipantTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "game-participant-test-signing-key-at-least-32-bytes",
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

    [SkippableFact]
    public async Task A_participant_can_read_the_state_and_the_legal_moves()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var owner = await RegisterAsync("participant-owner@example.com", "Participant Owner");
        Authorize(owner.Token);
        var gameId = await CreateGameAsync();

        var state = await _client!.GetAsync($"/api/games/{gameId}");
        var moves = await _client.GetAsync($"/api/games/{gameId}/legal-moves");

        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal(HttpStatusCode.OK, moves.StatusCode);

        // The guard must not have narrowed what a player of the game actually gets back. Twenty legal
        // moves from the opening position is the standard count, and asserting it rather than just
        // "some array" is what would catch a guard that accidentally empties the list.
        var legal = await moves.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legal);
        Assert.Equal(20, legal!.Length);
    }

    [SkippableFact]
    public async Task A_stranger_gets_the_same_404_the_v1_twin_gives_for_the_state()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var owner = await RegisterAsync("participant-state-owner@example.com", "State Owner");
        Authorize(owner.Token);
        var gameId = await CreateGameAsync();

        var stranger = await RegisterAsync("participant-state-stranger@example.com", "State Stranger");
        Authorize(stranger.Token);

        var response = await _client!.GetAsync($"/api/games/{gameId}");

        // NotFound, matching PublicApiEndpoints' /api/v1/games/{id}, which answers a non-participant
        // on an ongoing game with Results.NotFound(). Deliberately not Forbidden.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_stranger_gets_the_same_404_the_v1_twin_gives_for_the_legal_moves()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var owner = await RegisterAsync("participant-moves-owner@example.com", "Moves Owner");
        Authorize(owner.Token);
        var gameId = await CreateGameAsync();

        var stranger = await RegisterAsync("participant-moves-stranger@example.com", "Moves Stranger");
        Authorize(stranger.Token);

        var response = await _client!.GetAsync($"/api/games/{gameId}/legal-moves");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Belt and braces: a 404 whose body still carried the move list would be the same leak with a
        // different status on it.
        Assert.DoesNotContain("e2e4", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Playing_names_the_callers_live_game_and_nothing_for_someone_who_has_none()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var owner = await RegisterAsync("playing-owner@example.com", "Playing Owner");
        Authorize(owner.Token);
        var gameId = await CreateGameAsync();

        var mine = await _client!.GetAsync("/api/games/playing");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var body = await mine.Content.ReadFromJsonAsync<CurrentGameDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(gameId, body!.GameId);
        Assert.True(body.Solo);
        Assert.Null(body.Pool);

        var stranger = await RegisterAsync("playing-stranger@example.com", "Playing Stranger");
        Authorize(stranger.Token);
        var theirs = await _client.GetAsync("/api/games/playing");
        Assert.Equal(HttpStatusCode.NoContent, theirs.StatusCode);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void Authorize(string jwt) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

    /// <summary>
    /// Creates a solo game (no OpponentId, so the caller owns both sides, per CreateGameRequest). One
    /// account is enough to reproduce the finding: what matters is that the game is live and that the
    /// caller in the assertions is not one of its two player ids.
    /// </summary>
    private async Task<Guid> CreateGameAsync()
    {
        var response = await _client!.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "burn2",
            Rated = false
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<GameIdDto>(JsonOptions);
        Assert.NotNull(state);
        return state!.GameId;
    }

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        // Registration authorizes the client as a side effect of nothing - the header is set by the
        // caller - so this deliberately leaves the Authorization header alone.
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

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record GameIdDto(Guid GameId);

    private sealed record CurrentGameDto(Guid GameId, string? Pool, bool Solo);
}
