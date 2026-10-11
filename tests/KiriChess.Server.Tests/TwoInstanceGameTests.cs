using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8.4 Task B2 Step 2: the one test in this repo that has ever run two instances against each
/// other. Every other claim about the forwarding RPC is proven one level below where it actually
/// happens (<c>GameOwnershipTests</c>, <c>GameCommandRoutingTests</c>, against fakes). This is the
/// end-to-end proof that a command submitted on a non-owner really does cross the wire over Redis,
/// execute exactly once on the owner, and broadcast back to a connection sitting on a different
/// instance, without the game migrating. Requires Docker; skips cleanly when no daemon is reachable.
///
/// Two <see cref="WebApplicationFactory{Program}"/> hosts share one Postgres container (for the game
/// row and auth) and one Redis container (for the lease, the live-state index, the command forwarding
/// channels and the SignalR backplane), each given a distinct <see cref="InstanceIdentity"/>. See
/// <see cref="BuildFactory"/> for why Redis has to be supplied through <b>both</b>
/// <c>ConfigureAppConfiguration</c> and <c>ConfigureTestServices</c>: configuration is what attaches
/// the backplane, and only the service override can pin a deterministic instance id.
///
/// <b>What this does not prove, stated here so nobody reads more into it.</b> The SignalR backplane is
/// <b>not</b> attached in these hosts, and cannot be: <c>AddKiriChessBackplane</c> is a service
/// registration driven by Program.cs's pre-<c>Build()</c> <see cref="RedisOptions"/> read, and no
/// <see cref="WebApplicationFactory{Program}"/> hook lands before that, configuration supplied to the
/// factory arrives too late, and <c>ConfigureTestServices</c> later still. The assertion in the test
/// body pins that, so this comment cannot quietly go stale.
///
/// So what is proven is the <b>forwarding RPC</b>: a command submitted on a non-owner crosses Redis,
/// executes exactly once on the owner, and the owner's own broadcast reaches a connection sitting on
/// it. What is not proven is that a broadcast reaches a connection on a <i>different</i> host, White
/// sits on the owning host throughout, so this test would pass identically with no backplane at all.
/// Proving that needs the backplane reachable from a test host, which needs a seam Program.cs does not
/// currently have.
/// </summary>
public sealed class TwoInstanceGameTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string JwtKey = "e2e-test-signing-key-at-least-32-bytes-long";

    private readonly string _prefix = $"test-{Guid.NewGuid():N}:";
    private PostgreSqlContainer? _pg;
    private RedisContainer? _redis;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
    }

    [SkippableFact]
    public async Task Two_instances_share_one_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (host1, connection1) = BuildFactory("instance-a");
        var (host2, connection2) = BuildFactory("instance-b");
        await using var _1 = host1;
        await using var _2 = host2;
        using var _3 = connection1;
        using var _4 = connection2;

        using (var scope = host1.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        // InstanceHeartbeat never runs here, Program.cs only registers it when its own eagerly-read
        // RedisOptions.Enabled was true at startup, which (unavoidably; see BuildFactory) it was not for
        // either of these hosts. Register each instance's liveness key by hand, the same write
        // InstanceHeartbeat.StartAsync would have made, so GameActionExecutor.ResolveOwnerOrTakeoverAsync
        // sees host 1 as live instead of taking its game over out from under it.
        await host1.Services.GetRequiredService<InstanceRegistry>().RegisterAsync();
        await host2.Services.GetRequiredService<InstanceRegistry>().RegisterAsync();

        using var client1 = host1.CreateClient();

        var white = await RegisterAsync(client1, "two-instance-white@example.com", "White Player");
        var black = await RegisterAsync(client1, "two-instance-black@example.com", "Black Player");

        // Phase 9d.8 closed POST /api/games' OpponentId hole (README scope decision 5): a
        // two-distinct-player game is now created the way a real player would, via a challenge. Both
        // calls go through client1 (host1), same as the direct-create path did, so host1's registry is
        // still the one that claims the lease - the thing Step 1 below asserts.
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var challengeResponse = await client1.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "bounty1",
            Colour = "White"
        }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", black.Token);
        var acceptResponse = await client1.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(accepted);
        var gameId = accepted!.GameId;

        // Step 1: the host that created the game must own it, GameRegistry.CreateAsync claims a
        // brand-new lease unconditionally (README: a fresh Guid can never already be owned).
        var ownership1 = host1.Services.GetRequiredService<GameOwnership>();
        Assert.Equal("instance-a", await ownership1.GetOwnerAsync(gameId));

        // Pin the limitation rather than leaving a reader to assume otherwise: these hosts have the
        // DEFAULT hub lifetime manager, not the Redis one. AddKiriChessBackplane runs off the
        // pre-Build() RedisOptions read, which no WebApplicationFactory hook can reach, so the
        // backplane cannot be attached here however Redis is supplied. Everything below therefore
        // proves the forwarding RPC and nothing about cross-instance broadcast delivery, and if
        // somebody ever makes the backplane reachable from a test host, this assertion fails and
        // points them at the paragraph that says what to strengthen.
        Assert.IsType<DefaultHubLifetimeManager<GameHub>>(
            host2.Services.GetRequiredService<HubLifetimeManager<GameHub>>());

        await using var whiteConnection = BuildHubConnection(host1, white.Token);
        await using var blackConnection = BuildHubConnection(host2, black.Token);

        var whiteMoveEvents = new List<MovePlayedDto>();
        var whiteMoveLock = new object();
        whiteConnection.On<MovePlayedDto>("MovePlayed", dto =>
        {
            lock (whiteMoveLock) whiteMoveEvents.Add(dto);
        });

        await whiteConnection.StartAsync();
        await blackConnection.StartAsync();

        // Step 2: White joins on host 1, the local path, since host 1's registry already holds the
        // session it just created.
        var whiteSnapshot = await whiteConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        Assert.NotEmpty(whiteSnapshot.LegalMoves);

        // Step 3: Black joins on host 2, which has no local session for this game. This is the
        // forwarded path: host 2 resolves the owner as host 1 (a live instance, per the registration
        // above) and forwards JoinGame rather than taking the game over.
        var blackSnapshot = await blackConnection.InvokeAsync<GameSnapshotDto>("JoinGame", gameId.ToString());
        Assert.NotNull(blackSnapshot);

        // White moves first (standard rules), local on host 1, and not itself the claim under test;
        // it exists only to put Black on move and to hand this test Black's actual legal moves via the
        // broadcast, without a second HTTP round trip.
        await whiteConnection.InvokeAsync("SubmitMove", gameId.ToString(), whiteSnapshot.LegalMoves[0]);
        await WaitForCountAsync(whiteMoveEvents, whiteMoveLock, 1);
        var blackLegalMove = whiteMoveEvents[0].LegalMoves[0];

        // Step 4: Black submits a legal move through host 2, the forwarded path again, this time for
        // SubmitMove. Host 2 has no local session, resolves host 1 as the (still live) owner, and
        // routes the command over Redis pub/sub; host 1 executes it through the exact same
        // GameActionExecutor.SubmitMoveLocalAsync a local caller would have hit.
        await blackConnection.InvokeAsync("SubmitMove", gameId.ToString(), blackLegalMove);

        // Step 5: the phase's whole claim. White's connection sits on host 1, the owner; it must
        // receive the MovePlayed broadcast for a move that was actually applied on host 1 by a command
        // that arrived from host 2 over the router.
        await WaitForCountAsync(whiteMoveEvents, whiteMoveLock, 2);
        Assert.Equal(blackLegalMove, whiteMoveEvents[1].Uci);

        // Step 6: the game must not have migrated (README scope decision 2 / TASK-B2's whole point of
        // Step 1's fix), host 1 still owns it after a move forwarded from host 2.
        Assert.Equal("instance-a", await ownership1.GetOwnerAsync(gameId));
    }

    [SkippableFact]
    public async Task A_gracefully_stopped_owner_hands_its_games_over_at_once()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (host1, host2, connection1, connection2, gameId, black) = await SetUpOwnedGameAsync("graceful");
        await using var _1 = host1;
        await using var _2 = host2;
        using var _3 = connection1;
        using var _4 = connection2;

        var ownership1 = host1.Services.GetRequiredService<GameOwnership>();
        Assert.Equal("instance-a", await ownership1.GetOwnerAsync(gameId));

        // Exactly what InstanceHeartbeat.StopAsync now does on a graceful stop, in that order: run the
        // shutdown hooks (GameOwnershipHeartbeat releases every lease it holds), then deregister.
        await host1.Services.GetRequiredService<GameOwnershipHeartbeat>().OnInstanceStoppingAsync(CancellationToken.None);
        await host1.Services.GetRequiredService<InstanceRegistry>().DeregisterAsync();

        // C-5-3's first half: no lease is left behind for the TTL to clean up.
        Assert.Null(await ownership1.GetOwnerAsync(gameId));

        // And the game is immediately recoverable on the sibling, rather than after InstanceTtlSeconds.
        var snapshot = await host2.Services.GetRequiredService<IGameActionExecutor>()
            .JoinGameAsync(gameId, black.UserId, "conn-black-graceful");
        Assert.NotNull(snapshot);
        Assert.Equal("instance-b", await ownership1.GetOwnerAsync(gameId));
    }

    [SkippableFact]
    public async Task A_dead_owners_surviving_lease_does_not_hide_the_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (host1, host2, connection1, connection2, gameId, black) = await SetUpOwnedGameAsync("ungraceful");
        await using var _1 = host1;
        await using var _2 = host2;
        using var _3 = connection1;
        using var _4 = connection2;

        var ownership1 = host1.Services.GetRequiredService<GameOwnership>();

        // The second half of C-5-3, and the case the shutdown release cannot reach: the owner's
        // heartbeat key is gone while its lease is not. A kill -9 lands here too, because the heartbeat
        // key is written at the top of a tick and the leases are renewed by a hook after it, so the two
        // do not lapse at the same instant.
        await host1.Services.GetRequiredService<InstanceRegistry>().DeregisterAsync();
        Assert.Equal("instance-a", await ownership1.GetOwnerAsync(gameId));

        // Before the fix this threw HubException("Game not found."): the dead-owner branch is the one
        // written for "provably dead, safe to migrate", and its SET NX claim could not get past the
        // dead owner's surviving lease, so it retried once and gave up. Nothing recovered the game in
        // the background either, because OrphanedGameSweeper skips a game that still has an owner.
        var snapshot = await host2.Services.GetRequiredService<IGameActionExecutor>()
            .JoinGameAsync(gameId, black.UserId, "conn-black-ungraceful");
        Assert.NotNull(snapshot);
        Assert.Equal("instance-b", await ownership1.GetOwnerAsync(gameId));
    }

    /// <summary>
    /// Two hosts sharing one Redis and one Postgres, with a real two-player game created on (and
    /// therefore owned by) host 1. Everything the two C-5-3 cases share, up to the point where they
    /// differ in how host 1 goes away. Each caller passes its own <paramref name="tag"/> so the two
    /// tests do not collide on Redis keys, email addresses or the Postgres rows behind them.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host1, WebApplicationFactory<Program> Host2,
        RedisConnection Connection1, RedisConnection Connection2, Guid GameId, AuthResult Black)> SetUpOwnedGameAsync(string tag)
    {
        var (host1, connection1) = BuildFactory("instance-a", $"{_prefix}{tag}:");
        var (host2, connection2) = BuildFactory("instance-b", $"{_prefix}{tag}:");

        using (var scope = host1.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        // InstanceHeartbeat never runs in these hosts (see BuildFactory), so register both by hand,
        // the same write InstanceHeartbeat.StartAsync would have made.
        await host1.Services.GetRequiredService<InstanceRegistry>().RegisterAsync();
        await host2.Services.GetRequiredService<InstanceRegistry>().RegisterAsync();

        using var client1 = host1.CreateClient();
        var white = await RegisterAsync(client1, $"c53-{tag}-white@example.com", $"White {tag}");
        var black = await RegisterAsync(client1, $"c53-{tag}-black@example.com", $"Black {tag}");

        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", white.Token);
        var challengeResponse = await client1.PostAsJsonAsync("/api/challenges", new { Pool = "bounty1", Colour = "White" }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", black.Token);
        var acceptResponse = await client1.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(accepted);

        return (host1, host2, connection1, connection2, accepted!.GameId, black);
    }

    /// <summary>
    /// Polls rather than awaiting a fresh <see cref="TaskCompletionSource{TResult}"/> per event, the
    /// same idiom <c>GameOwnershipTests.Reconnect_notice_cancels_a_pending_abandon_and_broadcasts_exactly_once</c>
    /// uses for a hub broadcast that a fake proxy delivers synchronously up to its own first await: here
    /// the broadcast crosses two real hosts and a real Redis round trip, so this only needs to wait out
    /// real scheduling, never anything resembling the lease TTL.
    /// </summary>
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
    /// Program.cs reads <c>RedisOptions.From(builder.Configuration)</c> before <c>Build()</c> (its own
    /// comment on that line explains why: the SignalR backplane and the Data Protection key ring are
    /// service registrations with no post-Build() attachment point), so a test cannot toggle Redis on
    /// through <c>ConfigureAppConfiguration</c> the way it can every other setting, that value is
    /// already read by the time a test's configuration override is merged in. <c>ConfigureTestServices</c>
    /// works instead: it appends to the same <see cref="IServiceCollection"/> Program.cs already
    /// populated, and the last registration for a given service type wins at resolution time, so a real,
    /// enabled <see cref="IRedisConnection"/> (plus the matching <see cref="RedisOptions"/> and
    /// <see cref="InstanceIdentity"/>) overrides Program.cs's own disabled ones for every lazily-resolved
    /// singleton that depends on them (<see cref="GameOwnership"/>, <see cref="GameActionExecutor"/>,
    /// <see cref="GameCommandRouter"/>, <see cref="InstanceRegistry"/>, ...).
    ///
    /// What this cannot fix: the *conditional* registrations Program.cs makes based on its own
    /// eagerly-computed (and here, always-disabled) <c>redisOptions.Enabled</c>, the SignalR Redis
    /// backplane, the <c>InstanceHeartbeat</c> hosted service, and <c>OrphanedGameSweeper</c> never
    /// attach in this test. The backplane is not needed for this test's required proof: White's
    /// connection lives directly on host 1, the owner, so <c>Clients.Group(...)</c> reaches it without
    /// crossing instances. <c>InstanceHeartbeat</c>'s absence is worked around explicitly in the test
    /// body by calling <see cref="InstanceRegistry.RegisterAsync"/> once per host.
    /// </summary>
    private (WebApplicationFactory<Program> Factory, RedisConnection Connection) BuildFactory(string instanceId, string? keyPrefix = null)
    {
        var identity = new InstanceIdentity(instanceId);
        var options = new RedisOptions(_redis!.GetConnectionString(), keyPrefix ?? _prefix, InstanceTtlSeconds: 60, HeartbeatSeconds: 5, CommandTimeoutSeconds: 5);
        var connection = new RedisConnection(options, identity);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = JwtKey,
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                    // Deliberately no Redis:* here. It would be dead configuration: Program.cs reads
                    // RedisOptions before Build() and a factory's in-memory configuration lands after
                    // that, so the only thing it would achieve is making a reader believe the
                    // backplane is on. The live connection goes in through ConfigureTestServices
                    // below, which is enough for everything resolved from DI and nothing more.
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(options);
                services.AddSingleton(identity);
                services.AddSingleton<IRedisConnection>(connection);
            });
        });

        return (factory, connection);
    }

    private static HubConnection BuildHubConnection(WebApplicationFactory<Program> factory, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/game"), options =>
            {
                // TestServer's in-memory transport does not carry real sockets, so long polling over
                // Server.CreateHandler() is what lets a HubConnection talk to a WebApplicationFactory
                // host at all, this is the same handler CreateClient() hands to HttpClient.
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task<AuthResult> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = displayName,
            // Phase 8a.2: register refuses without both consent flags. This file landed from the 8.4
            // branch after 8a.2's test sweep had already run, so it was the one register call site the
            // sweep could not have seen.
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
