using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Analytics;
using KiriChess.Server.Data;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 11a.1 rematch offer/accept: colours swap, rated/pool carry over from the finished game, a
/// caller can't accept their own offer, and the guarded claim really does resolve a two-accepts race to
/// exactly one winner. Exercises <see cref="RematchService"/> directly against a real Postgres rather
/// than through <see cref="GameHub"/>'s SignalR transport - the property under test is the
/// <c>ExecuteUpdateAsync</c> guard's correctness against real concurrent writers, which is a database
/// fact, not a hub-wiring one; <see cref="HubContractTests"/> already covers the wire shape.
/// Requires Docker; skips cleanly when no Docker daemon is reachable. Fixture scaffolding is copied
/// verbatim from <see cref="RatingPersistenceTests"/>.
/// </summary>
public sealed class RematchTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "rematch-test-signing-key-at-least-32-bytes-long",
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
    public async Task Accepted_offer_swaps_colours_and_carries_over_rated_and_pool()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        await rematches.OfferAsync(gameId, white.UserId);
        var candidate = await rematches.PeekOpenOfferAsync(gameId, black.UserId);
        Assert.NotNull(candidate);
        Assert.True(candidate!.Rated);

        var acceptance = await rematches.TryClaimAsync(gameId, black.UserId);
        Assert.NotNull(acceptance);
        Assert.Equal(black.UserId, acceptance!.NewWhitePlayerId);
        Assert.Equal(white.UserId, acceptance.NewBlackPlayerId);
        Assert.Equal("steal1", acceptance.Pool);
        Assert.True(acceptance.Rated);
    }

    [SkippableFact]
    public async Task Cannot_accept_your_own_offer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        await rematches.OfferAsync(gameId, white.UserId);
        var candidate = await rematches.PeekOpenOfferAsync(gameId, white.UserId);
        Assert.Null(candidate);
    }

    [SkippableFact]
    public async Task Two_concurrent_accepts_produce_exactly_one_winner()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using (var offerScope = _factory!.Services.CreateScope())
        {
            await offerScope.ServiceProvider.GetRequiredService<RematchService>()
                .OfferAsync(gameId, white.UserId);
        }

        // Two separate DI scopes, each with its own KiriChessDbContext, the same isolation two
        // different GameHub invocations (possibly on two different instances) would each get. Racing
        // both TryClaimAsync calls at once is what actually exercises the guarded UPDATE rather than
        // just proving a second sequential call sees AcceptedAt already set.
        using var scopeA = _factory!.Services.CreateScope();
        using var scopeB = _factory!.Services.CreateScope();
        var rematchA = scopeA.ServiceProvider.GetRequiredService<RematchService>();
        var rematchB = scopeB.ServiceProvider.GetRequiredService<RematchService>();

        var results = await Task.WhenAll(
            rematchA.TryClaimAsync(gameId, black.UserId),
            rematchB.TryClaimAsync(gameId, black.UserId));

        Assert.Single(results, r => r is not null);

        using var verifyScope = _factory!.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var offer = await db.RematchOffers.SingleAsync(r => r.GameId == gameId);
        Assert.Equal(black.UserId, offer.AcceptedById);

        var accept = await db.DailyCounters.AsNoTracking()
            .SingleAsync(c => c.CounterKey == CounterKeys.RematchAccept);
        Assert.Equal(1, accept.Count);
        Assert.Equal(CounterSources.Direct, accept.Source);
    }

    /// <summary>
    /// P-3-1's interim mitigation. The player who arrives from the rematch mail used to be able to
    /// press Offer and silently turn the standing offer around to point at themselves, at which point
    /// neither player could accept (nobody may accept their own offer) and the only escape was for one
    /// of them to press Offer again, turning it around once more. A recoverable state became a
    /// ping-pong. The offer must survive the press.
    /// </summary>
    [SkippableFact]
    public async Task Offering_against_the_other_players_standing_offer_is_refused_and_leaves_it_intact()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        await rematches.OfferAsync(gameId, white.UserId);

        var refused = await Assert.ThrowsAsync<InvalidGameActionException>(
            () => rematches.OfferAsync(gameId, black.UserId));
        Assert.Contains("Accept it instead", refused.Message);

        // The point of the mitigation: the offer still points the way it did, so black can still act
        // on it. Before the fix this peek returned null, because the row now named black as offerer.
        var candidate = await rematches.PeekOpenOfferAsync(gameId, black.UserId);
        Assert.NotNull(candidate);

        using var verifyScope = _factory!.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var offer = await db.RematchOffers.AsNoTracking().SingleAsync(r => r.GameId == gameId);
        Assert.Equal(white.UserId, offer.OfferedById);
    }

    /// <summary>
    /// P-3-1 / P-3-1a. After the live session is evicted, first-party GET must still load the
    /// finished game for a participant and carry who offered, so the mail recipient can Accept.
    /// A stranger still 404s: RematchOfferedById is not a public fact, and replay is the public read.
    /// </summary>
    [SkippableFact]
    public async Task Finished_game_get_carries_the_standing_offer_for_a_participant_and_404s_a_stranger()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using (var scope = _factory!.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RematchService>().OfferAsync(gameId, white.UserId);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", black.Token);
        var asRecipient = await _client.GetAsync($"/api/games/{gameId}");
        Assert.Equal(HttpStatusCode.OK, asRecipient.StatusCode);
        var recipientState = await asRecipient.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.NotNull(recipientState);
        Assert.NotEqual("Ongoing", recipientState!.Status);
        Assert.Equal(white.UserId, recipientState.RematchOfferedById);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var asOfferer = await _client.GetAsync($"/api/games/{gameId}");
        asOfferer.EnsureSuccessStatusCode();
        var offererState = await asOfferer.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.Equal(white.UserId, offererState!.RematchOfferedById);

        var stranger = await RegisterAsync($"rematch-get-stranger@example.com", "Rematch Stranger");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stranger.Token);
        var asStranger = await _client.GetAsync($"/api/games/{gameId}");
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
    }

    /// <summary>
    /// P-3-1a. JoinGame on a finished game used to throw "Game not found." The durable read must
    /// return a snapshot with the standing offer and must not put the session back in the registry.
    /// </summary>
    [SkippableFact]
    public async Task JoinGame_on_a_finished_game_returns_the_standing_offer_without_restoring_the_session()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using (var scope = _factory!.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RematchService>().OfferAsync(gameId, white.UserId);

        Assert.Null(_factory.Services.GetRequiredService<GameRegistry>().Get(gameId));

        await using var blackConnection = BuildGameHubConnection(black.Token);
        await blackConnection.StartAsync();
        var snapshot = await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());

        Assert.Equal(white.UserId, snapshot.State.RematchOfferedById);
        Assert.Empty(snapshot.LegalMoves);
        Assert.NotEqual("Ongoing", snapshot.State.Status);
        Assert.Null(_factory.Services.GetRequiredService<GameRegistry>().Get(gameId));
    }

    [SkippableFact]
    public async Task JoinGame_on_a_finished_game_still_refuses_a_stranger()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (_, _, gameId) = await FinishRatedGameAsync();
        var stranger = await RegisterAsync($"rematch-join-stranger@example.com", "Join Stranger");

        await using var connection = BuildGameHubConnection(stranger.Token);
        await connection.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString()));
        // The SignalR client prefixes a hub-thrown HubException after an awaited catch with
        // "An unexpected error occurred invoking 'JoinGame' on the server." The payload is still
        // the same refusal the live non-participant path uses.
        Assert.Contains("Game not found.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// P-3-1a without an offer: the finished page still has to render. GET used to 404 the instant
    /// the session left the registry, which is every game once it ends.
    /// </summary>
    [SkippableFact]
    public async Task Finished_game_get_succeeds_for_a_participant_when_nobody_has_offered()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var response = await _client.GetAsync($"/api/games/{gameId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.NotNull(state);
        Assert.NotEqual("Ongoing", state!.Status);
        Assert.Null(state.RematchOfferedById);
    }

    /// <summary>
    /// C-6-3's repeat guard. Re-offering used to overwrite the row and return, which re-armed the mail
    /// and fired another push every time, so one caller could multiply notices at will.
    /// </summary>
    [SkippableFact]
    public async Task Re_offering_your_own_standing_offer_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        var first = await rematches.OfferAsync(gameId, white.UserId);
        Assert.True(first.IsNewOffer);

        var refused = await Assert.ThrowsAsync<InvalidGameActionException>(
            () => rematches.OfferAsync(gameId, white.UserId));
        Assert.Contains("already offered", refused.Message);
    }

    /// <summary>
    /// C-6-3's recency bound: without it, every game either player ever played stays an open channel
    /// to notify them, forever.
    ///
    /// The game's <c>EndedAt</c> is moved back rather than the clock moved forward, and that is
    /// deliberate rather than a shortcut past <see cref="FakeGameClock"/>. The bound is a subtraction
    /// between two things: an instant persisted by <see cref="GameSession"/>, which builds its own
    /// <see cref="SystemGameClock"/> rather than taking the injected one, and <see cref="IGameClock.UtcNow"/>
    /// read by <see cref="RematchService"/>. Swapping only the injectable half for a fake would move
    /// one side of that subtraction and not the other, and the test would pass or fail for a reason
    /// unrelated to the window. Backdating the row moves the interval honestly and deterministically,
    /// with no sleeping and no dependence on how long the fixture took to get here.
    /// </summary>
    [SkippableFact]
    public async Task Offer_is_refused_once_the_game_finished_outside_the_offer_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        var ttlMinutes = _factory!.Services
            .GetRequiredService<IOptions<ChallengeOptions>>().Value.TtlMinutes;

        using (var backdateScope = _factory!.Services.CreateScope())
        {
            var db = backdateScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var staleEndedAt = DateTimeOffset.UtcNow.AddMinutes(-(ttlMinutes + 1));
            await db.Games.Where(g => g.Id == gameId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.EndedAt, staleEndedAt));
        }

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        var refused = await Assert.ThrowsAsync<InvalidGameActionException>(
            () => rematches.OfferAsync(gameId, white.UserId));
        Assert.Contains("too long ago", refused.Message);

        using var verifyScope = _factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.False(await verifyDb.RematchOffers.AnyAsync(r => r.GameId == gameId));
    }

    /// <summary>
    /// A game that finished just now is inside the window, so the bound above is a bound and not an
    /// outright break of the feature. Worth its own test: an off-by-one in the comparison direction
    /// would leave every other rematch test passing.
    /// </summary>
    [SkippableFact]
    public async Task Offer_on_a_game_that_just_finished_is_inside_the_offer_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        var outcome = await rematches.OfferAsync(gameId, white.UserId);
        Assert.True(outcome.IsNewOffer);
        Assert.Equal(black.UserId, outcome.OpponentId);
    }

    /// <summary>
    /// The other half of C-6-3's one-shot fan-out: an offer landing on an already-accepted game stays
    /// the silent no-op it always was, but now reports it, so <see cref="GameHub.OfferRematch"/> does
    /// not mail and push over a rematch that is already underway.
    /// </summary>
    [SkippableFact]
    public async Task Offer_on_an_already_accepted_game_reports_no_new_offer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();

        await rematches.OfferAsync(gameId, white.UserId);
        Assert.NotNull(await rematches.TryClaimAsync(gameId, black.UserId));

        var outcome = await rematches.OfferAsync(gameId, white.UserId);
        Assert.False(outcome.IsNewOffer);
    }

    [SkippableFact]
    public async Task New_offer_records_rematch_offer_once_direct()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();
        var outcome = await rematches.OfferAsync(gameId, white.UserId);
        Assert.True(outcome.IsNewOffer);

        var (count, source) = await CounterAsync(CounterKeys.RematchOffer);
        Assert.Equal(1, count);
        Assert.Equal(CounterSources.Direct, source);
        Assert.Equal(0, (await CounterAsync(CounterKeys.RematchAccept)).Count);
    }

    [SkippableFact]
    public async Task Repeat_own_offer_does_not_increment_rematch_offer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, _, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();
        await rematches.OfferAsync(gameId, white.UserId);
        await Assert.ThrowsAsync<InvalidGameActionException>(
            () => rematches.OfferAsync(gameId, white.UserId));

        Assert.Equal(1, (await CounterAsync(CounterKeys.RematchOffer)).Count);
    }

    [SkippableFact]
    public async Task Successful_claim_records_rematch_accept_once_direct()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using var scope = _factory!.Services.CreateScope();
        var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();
        await rematches.OfferAsync(gameId, white.UserId);
        Assert.NotNull(await rematches.TryClaimAsync(gameId, black.UserId));

        var offer = await CounterAsync(CounterKeys.RematchOffer);
        var accept = await CounterAsync(CounterKeys.RematchAccept);
        Assert.Equal(1, offer.Count);
        Assert.Equal(1, accept.Count);
        Assert.Equal(CounterSources.Direct, accept.Source);
    }

    [SkippableFact]
    public async Task Persist_of_a_finished_game_with_ply_records_first_game_finish()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await FinishRatedGameAsync();

        var (count, source) = await CounterAsync(CounterKeys.FirstGameFinish);
        Assert.Equal(1, count);
        Assert.Equal(CounterSources.Direct, source);
    }

    /// <summary>Registers two players, plays a rated game to a resignation, and returns (white, black, gameId).</summary>
    private async Task<(AuthResult White, AuthResult Black, Guid GameId)> FinishRatedGameAsync(
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var white = await RegisterAsync($"rematch-{testName}-white@example.com", "Rematch White");
        var black = await RegisterAsync($"rematch-{testName}-black@example.com", "Rematch Black");

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

        return (white, black, gameId);
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

    private async Task<(long Count, string? Source)> CounterAsync(string key)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.DailyCounters.AsNoTracking()
            .SingleOrDefaultAsync(c => c.CounterKey == key);
        return (row?.Count ?? 0, row?.Source);
    }

    private HubConnection BuildGameHubConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);
}
