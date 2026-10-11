using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.Email;
using KiriChess.Server.Infrastructure.Push;
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
/// The 11b.1 follow-up: web push. <c>VapidOptions.Enabled</c> itself is a pure computed property and
/// covered without Docker in <see cref="VapidOptionsTests"/>; everything here needs a real database and
/// is skipped cleanly when no Docker daemon is reachable. VAPID is configured with a fake key pair, and
/// <see cref="FakePushSender"/> replaces the real <c>WebPushSender</c> so no test ever calls an actual
/// push service - the same substitution <c>RematchNotificationTests</c> makes for <c>IEmailSender</c>.
/// Fixture scaffolding copied verbatim from <see cref="RematchNotificationTests"/>.
/// </summary>
public sealed class PushNotificationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private FakePushSender? _push;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        _push = new FakePushSender();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "push-notification-test-signing-key-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                });
            });
            builder.ConfigureServices(services =>
            {
                // VapidOptions itself has to be overridden here rather than via config above:
                // Program.cs computes it with an eager VapidOptions.From(builder.Configuration) read
                // that runs before builder.Build(), earlier than WebApplicationFactory's own config
                // hook splices this fixture's in-memory collection in - an eager read never sees it.
                // Last registration wins for single-consumer resolution, the same substitution
                // RematchNotificationTests makes for IEmailSender, and it is enough on its own: nothing
                // downstream re-reads Vapid:* from IConfiguration once PushNotificationService and
                // PushEndpoints hold this injected instance.
                services.AddSingleton(new VapidOptions("test-public-key", "test-private-key", "mailto:push-test@example.com"));
                services.AddSingleton<IPushSender>(_push);
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
    public async Task Vapid_public_key_endpoint_reports_enabled_with_the_configured_key()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/push/vapid-public-key");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<VapidPublicKeyDto>(JsonOptions);

        Assert.NotNull(dto);
        Assert.True(dto!.Enabled);
        Assert.Equal("test-public-key", dto.PublicKey);
    }

    [SkippableFact]
    public async Task Subscribing_without_auth_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var anonymousClient = _factory!.CreateClient();
        var response = await anonymousClient.PostAsJsonAsync("/api/push/subscribe", new
        {
            Endpoint = "https://fcm.googleapis.com/fcm/send/anon",
            P256dh = "p256dh-key",
            Auth = "auth-secret"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Subscribe_then_unsubscribe_round_trips_cleanly()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("push-roundtrip@example.com", "PushRoundtrip");
        AuthorizeAs(me);
        const string endpoint = "https://fcm.googleapis.com/fcm/send/roundtrip";

        var subscribeResponse = await _client!.PostAsJsonAsync("/api/push/subscribe", new
        {
            Endpoint = endpoint,
            P256dh = "p256dh-key",
            Auth = "auth-secret"
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, subscribeResponse.StatusCode);
        Assert.Equal(1, await SubscriptionCountAsync(endpoint));

        var unsubscribeResponse = await _client.PostAsJsonAsync("/api/push/unsubscribe", new { Endpoint = endpoint }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, unsubscribeResponse.StatusCode);
        Assert.Equal(0, await SubscriptionCountAsync(endpoint));
    }

    /// <summary>
    /// S-1-8: the endpoint-level half of <see cref="PushEndpointPolicyTests"/>. The refusal has to
    /// happen before the row is written, because the row is what <c>WebPushSender</c> later POSTs to
    /// from inside the container; asserting the count is still zero is what proves nothing was
    /// stored to fail later.
    /// </summary>
    [SkippableTheory]
    [InlineData("https://internal.flycast/admin", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "linklocal")]
    [InlineData("http://fcm.googleapis.com/fcm/send/plaintext", "plaintext")]
    public async Task Subscribing_to_an_endpoint_outside_the_push_service_allowlist_is_refused(
        string endpoint, string label)
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Distinct per case: DisplayName carries a unique index, so a shared name fails inside the
        // register helper rather than at the assertion below.
        var me = await RegisterAsync($"push-ssrf-{label}@example.com", $"PushSsrf{label}");
        AuthorizeAs(me);

        var response = await _client!.PostAsJsonAsync("/api/push/subscribe", new
        {
            Endpoint = endpoint,
            P256dh = "p256dh-key",
            Auth = "auth-secret"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await SubscriptionCountAsync(endpoint));
    }

    [SkippableFact]
    public async Task Unsubscribing_an_endpoint_never_owned_still_returns_no_content()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("push-unsub-noop@example.com", "PushUnsubNoop");
        AuthorizeAs(me);

        var response = await _client!.PostAsJsonAsync("/api/push/unsubscribe", new { Endpoint = "https://fcm.googleapis.com/fcm/send/never-subscribed" }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [SkippableFact]
    public async Task Accepting_a_challenge_pushes_to_every_subscription_the_challenger_holds()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("push-pairing-challenger@example.com", "PushChallenger");
        AuthorizeAs(challenger);
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/pairing-1");
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/pairing-2");

        var accepter = await RegisterAsync("push-pairing-accepter@example.com", "PushAccepter");

        _client!.DefaultRequestHeaders.Authorization = new("Bearer", challenger.Token);
        var challengeResponse = await _client.PostAsJsonAsync("/api/challenges", new { Pool = "steal1", Colour = "White" }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);

        _client.DefaultRequestHeaders.Authorization = new("Bearer", accepter.Token);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();

        var sent = await WaitForSendsAsync(2);
        Assert.All(sent, payload => Assert.Contains("\"title\":\"Your opponent is ready\"", payload));
    }

    /// <summary>
    /// M-5-3. The mail half has honoured <see cref="NotificationCategory.PairingFound"/> since 9e.5;
    /// the push half did not, so unsubscribing from "your opponent is ready" mail left the matching
    /// push arriving anyway. Mirrors <see cref="Opponent_unsubscribed_from_rematch_notices_gets_no_push"/>
    /// for the other category.
    /// </summary>
    [SkippableFact]
    public async Task Challenger_unsubscribed_from_pairing_notices_gets_no_push()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("push-pairing-optout-challenger@example.com", "PushOptOutChallenger");
        AuthorizeAs(challenger);
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/pairing-optout");

        using (var scope = _factory!.Services.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
            await preferences.SetSubscribedAsync(challenger.UserId, NotificationCategory.PairingFound, subscribed: false);
        }

        var accepter = await RegisterAsync("push-pairing-optout-accepter@example.com", "PushOptOutAccepter");

        _client!.DefaultRequestHeaders.Authorization = new("Bearer", challenger.Token);
        var challengeResponse = await _client.PostAsJsonAsync("/api/challenges", new { Pool = "steal1", Colour = "White" }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);

        _client.DefaultRequestHeaders.Authorization = new("Bearer", accepter.Token);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();

        // The positive test above proves this push arrives well within five seconds when it is
        // allowed, so the same window elapsing with nothing sent is a real negative, not an untimed guess.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Empty(_push!.Sent);
    }

    [SkippableFact]
    public async Task Offering_a_rematch_pushes_to_the_opponent_immediately_not_after_the_delay()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("push-rematch-white@example.com", "PushRematchWhite");
        var black = await RegisterAsync("push-rematch-black@example.com", "PushRematchBlack");

        AuthorizeAs(black);
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/rematch");

        var gameId = await FinishRatedGameAsync(white, black);

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        // No Rematch:NotifyDelaySeconds override here at all - unlike the delayed mail, push must
        // arrive with no wait, which is the entire point of PushNotificationService not routing through
        // RematchNotificationService's timer. A short poll proves "immediately", a delay override
        // would not.
        var sent = await WaitForSendsAsync(1, timeoutSeconds: 5);
        Assert.Contains("\"title\":\"A rematch is waiting\"", sent[0]);
    }

    /// <summary>
    /// C-6-3's third clause. The mail half has honoured
    /// <see cref="NotificationCategory.RematchOffered"/> since 11b.1; the push half did not, so
    /// unsubscribing silenced the quiet transport and left the one that lights up a phone. A reader
    /// who turns this category off is asking not to be told, not asking to be told differently.
    /// </summary>
    [SkippableFact]
    public async Task Opponent_unsubscribed_from_rematch_notices_gets_no_push()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("push-rematch-optout-white@example.com", "PushOptOutWhite");
        var black = await RegisterAsync("push-rematch-optout-black@example.com", "PushOptOutBlack");

        AuthorizeAs(black);
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/rematch-optout");

        using (var scope = _factory!.Services.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
            await preferences.SetSubscribedAsync(black.UserId, NotificationCategory.RematchOffered, subscribed: false);
        }

        var gameId = await FinishRatedGameAsync(white, black);

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        // The positive test above proves this push arrives within five seconds when it is allowed,
        // so the same window elapsing with nothing sent is a real negative, not an untimed guess.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Empty(_push!.Sent);
    }

    [SkippableFact]
    public async Task A_gone_subscription_is_pruned_after_the_next_push_attempt()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("push-pruned@example.com", "PushPruned");
        AuthorizeAs(challenger);
        await SubscribeAsync("https://fcm.googleapis.com/fcm/send/gone");
        _push!.OutcomeOverride = _ => PushSendOutcome.Gone;

        var accepter = await RegisterAsync("push-pruned-accepter@example.com", "PushPrunedAccepter");

        _client!.DefaultRequestHeaders.Authorization = new("Bearer", challenger.Token);
        var challengeResponse = await _client.PostAsJsonAsync("/api/challenges", new { Pool = "steal1", Colour = "White" }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);

        _client.DefaultRequestHeaders.Authorization = new("Bearer", accepter.Token);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();

        await WaitForSendsAsync(1);
        Assert.Equal(0, await SubscriptionCountAsync("https://fcm.googleapis.com/fcm/send/gone"));
    }

    private async Task<int> SubscriptionCountAsync(string endpoint)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.WebPushSubscriptions.CountAsync(s => s.Endpoint == endpoint);
    }

    private async Task SubscribeAsync(string endpoint)
    {
        var response = await _client!.PostAsJsonAsync("/api/push/subscribe", new
        {
            Endpoint = endpoint,
            P256dh = "p256dh-key",
            Auth = "auth-secret"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string[]> WaitForSendsAsync(int count, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (_push!.Sent.Count >= count) return _push.Sent.Select(s => s.Payload).ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException($"Expected {count} push send(s), got {_push!.Sent.Count} within {timeoutSeconds}s.");
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    /// <summary>Plays a rated game to a resignation and returns the gameId. Rated two-player games come from matchmaking, not friend links.</summary>
    private async Task<Guid> FinishRatedGameAsync(AuthResult white, AuthResult black)
    {
        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var session = await registry.CreateAsync(
            KiriChessConfig.Presets["steal1"].Clone(), white.UserId, black.UserId, "steal1", rated: true);
        var gameId = session.Id;

        _client!.DefaultRequestHeaders.Authorization = new("Bearer", white.Token);
        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();

        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();

        return gameId;
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

    private void AuthorizeAs(AuthResult auth) =>
        _client!.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed class FakePushSender : IPushSender
    {
        public ConcurrentBag<(WebPushSubscription Subscription, string Payload)> Sent { get; } = new();

        /// <summary>Null (the default) always reports Sent. Tests that need to exercise pruning override this.</summary>
        public Func<WebPushSubscription, PushSendOutcome>? OutcomeOverride { get; set; }

        public Task<PushSendOutcome> SendAsync(WebPushSubscription subscription, string payloadJson, CancellationToken ct = default)
        {
            Sent.Add((subscription, payloadJson));
            return Task.FromResult(OutcomeOverride?.Invoke(subscription) ?? PushSendOutcome.Sent);
        }
    }
}

/// <summary>Pure computed-property coverage for <see cref="VapidOptions.Enabled"/> - no Docker, no database, runs everywhere.</summary>
public sealed class VapidOptionsTests
{
    [Fact]
    public void Enabled_requires_all_three_values()
    {
        Assert.False(new VapidOptions("", "", "").Enabled);
        Assert.False(new VapidOptions("pub", "", "mailto:a@b.com").Enabled);
        Assert.False(new VapidOptions("pub", "priv", "").Enabled);
        Assert.True(new VapidOptions("pub", "priv", "mailto:a@b.com").Enabled);
    }
}
