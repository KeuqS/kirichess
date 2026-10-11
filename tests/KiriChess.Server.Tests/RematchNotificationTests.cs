using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 11b.1's rematch-offer mail: a real <see cref="GameHub.OfferRematch"/> call over a real
/// SignalR connection, proving <see cref="RematchNotificationService"/>'s delayed check actually
/// fires, is suppressed once the offer is accepted, and is suppressed by the recipient's own mail
/// preference. <c>Rematch:NotifyDelaySeconds</c> is overridden to 1 so these run in seconds rather
/// than the production default. Requires Docker; skips cleanly when no Docker daemon is reachable.
/// Fixture scaffolding copied verbatim from <see cref="PairingNotificationTests"/> and
/// <see cref="HubConnectionBudgetEndToEndTests"/>'s connection helper.
/// </summary>
public sealed class RematchNotificationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private CapturingEmailSender? _email;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        var connectionString = _pg.GetConnectionString();
        _email = new CapturingEmailSender();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Jwt:Key"] = "rematch-notification-test-signing-key-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Rematch:NotifyDelaySeconds"] = "1",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEmailSender>(_email);
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
    public async Task Offer_left_unaccepted_emails_the_opponent_after_the_delay()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        var sent = await WaitForEmailAsync(EmailTemplates.RematchOfferedSubject);
        Assert.Equal(black.Email, sent.ToEmail);
        Assert.Contains($"/game/{gameId}", sent.TextBody);
    }

    [SkippableFact]
    public async Task Offer_accepted_before_the_delay_sends_no_email()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        await using var blackConn = BuildConnection(black.Token);
        await blackConn.StartAsync();
        await blackConn.InvokeAsync("AcceptRematch", gameId.ToString());

        // Long enough for the armed delay (1s) to have fired and run its check.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
    }

    [SkippableFact]
    public async Task Unsubscribed_opponent_gets_no_email()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);

        using (var scope = _factory!.Services.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
            await preferences.SetSubscribedAsync(black.UserId, NotificationCategory.RematchOffered, subscribed: false);
        }

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
    }

    /// <summary>
    /// C-6-3's fan-out clause, end to end. A burst of offers on one game used to produce one mail per
    /// call ninety seconds later, because each call overwrote the row and armed another timer that
    /// re-checked the same still-open offer. The repeat is now refused outright, and the mail is armed
    /// only for the call that actually created the offer, so the burst produces exactly one notice.
    /// </summary>
    [SkippableFact]
    public async Task A_burst_of_offers_on_one_game_sends_exactly_one_email()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);

        await using var whiteConn = BuildConnection(white.Token);
        await whiteConn.StartAsync();
        await whiteConn.InvokeAsync("OfferRematch", gameId.ToString());

        for (var i = 0; i < 5; i++)
        {
            var refused = await Assert.ThrowsAsync<HubException>(
                () => whiteConn.InvokeAsync("OfferRematch", gameId.ToString()));
            Assert.Contains("already offered", refused.Message);
        }

        await WaitForEmailAsync(EmailTemplates.RematchOfferedSubject);
        // Well past the armed delay (1s), so any extra timer the burst could have started has run.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
    }

    /// <summary>
    /// P-3-7: Arming persists when the delayed mail becomes sendable, so a restart inside the window
    /// still has something to sweep. DueAt is written from DateTimeOffset.UtcNow + delay (the
    /// notifier does not take IGameClock).
    /// </summary>
    [SkippableFact]
    public async Task Arming_a_new_offer_writes_NotificationDueAt_in_the_future()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();

        using (var scope = _factory!.Services.CreateScope())
        {
            var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();
            var outcome = await rematches.OfferAsync(gameId, white.UserId);
            Assert.True(outcome.IsNewOffer);
            Assert.Equal(black.UserId, outcome.OpponentId);
        }

        var beforeArm = DateTimeOffset.UtcNow;
        _factory.Services.GetRequiredService<RematchNotificationService>().Arm(gameId, black.UserId);

        var offer = await WaitForOfferAsync(gameId, r => r.NotificationDueAt is not null);
        Assert.True(offer.NotificationDueAt > beforeArm, $"DueAt was {offer.NotificationDueAt}, arm started at {beforeArm}.");
        Assert.Null(offer.NotificationSentAt);
    }

    /// <summary>
    /// P-3-7: the send claims NotificationSentAt with ExecuteUpdateAsync, so a second TrySendAsync
    /// (timer vs sweep, or a retried sweep) does not mail again.
    /// </summary>
    [SkippableFact]
    public async Task TrySendAsync_sets_NotificationSentAt_and_does_not_send_twice()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);
        await InsertOfferAsync(gameId, white.UserId);

        var notifications = _factory!.Services.GetRequiredService<RematchNotificationService>();
        await notifications.TrySendAsync(gameId, black.UserId);

        Assert.Single(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
        var offer = await LoadOfferAsync(gameId);
        Assert.NotNull(offer.NotificationSentAt);

        await notifications.TrySendAsync(gameId, black.UserId);
        Assert.Single(_email.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
    }

    /// <summary>
    /// P-3-7: a row that came due while no in-process timer was alive (the restart case) is sent
    /// once by FlushOwedAsync. Does not wait the production 90s; DueAt is already in the past.
    /// </summary>
    [SkippableFact]
    public async Task FlushOwedAsync_sends_once_for_a_past_due_unaccepted_offer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);
        await InsertOfferAsync(gameId, white.UserId, dueAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var notifications = _factory!.Services.GetRequiredService<RematchNotificationService>();
        await notifications.FlushOwedAsync();
        await notifications.FlushOwedAsync();

        Assert.Single(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
        var offer = await LoadOfferAsync(gameId);
        Assert.NotNull(offer.NotificationSentAt);
    }

    /// <summary>P-3-7: an accepted offer with a leftover due marker is not mailed.</summary>
    [SkippableFact]
    public async Task FlushOwedAsync_does_not_send_an_accepted_offer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (white, black, gameId) = await FinishRatedGameAsync();
        await ConfirmEmailAsync(black.UserId);
        await InsertOfferAsync(
            gameId, white.UserId,
            dueAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            acceptedById: black.UserId,
            acceptedAt: DateTimeOffset.UtcNow);

        await _factory!.Services.GetRequiredService<RematchNotificationService>().FlushOwedAsync();

        Assert.DoesNotContain(_email!.Sent, m => m.Subject == EmailTemplates.RematchOfferedSubject);
        var offer = await LoadOfferAsync(gameId);
        Assert.Null(offer.NotificationSentAt);
    }

    private async Task InsertOfferAsync(
        Guid gameId, Guid offeredById,
        DateTimeOffset? dueAt = null,
        Guid? acceptedById = null,
        DateTimeOffset? acceptedAt = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.RematchOffers.Add(new RematchOffer
        {
            GameId = gameId,
            OfferedById = offeredById,
            OfferedAt = DateTimeOffset.UtcNow,
            NotificationDueAt = dueAt,
            AcceptedById = acceptedById,
            AcceptedAt = acceptedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RematchOffer> LoadOfferAsync(Guid gameId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.RematchOffers.AsNoTracking().SingleAsync(r => r.GameId == gameId);
    }

    private async Task<RematchOffer> WaitForOfferAsync(
        Guid gameId, Func<RematchOffer, bool> predicate, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        RematchOffer? last = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            last = await db.RematchOffers.AsNoTracking().FirstOrDefaultAsync(r => r.GameId == gameId);
            if (last is not null && predicate(last)) return last;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException($"Rematch offer {gameId} did not satisfy the wait predicate within {timeoutSeconds}s. Last DueAt={last?.NotificationDueAt}.");
    }

    private async Task<EmailMessage> WaitForEmailAsync(string subject, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var found = _email!.Sent.FirstOrDefault(m => m.Subject == subject);
            if (found is not null) return found;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException($"No email with subject '{subject}' arrived within {timeoutSeconds}s.");
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

    private async Task ConfirmEmailAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
    }

    /// <summary>Registers two players, plays a rated game to a resignation, and returns (white, black, gameId).</summary>
    private async Task<(AuthResult White, AuthResult Black, Guid GameId)> FinishRatedGameAsync(
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var white = await RegisterAsync($"rematch-notify-{testName}-white@example.com", "RNWhite");
        var black = await RegisterAsync($"rematch-notify-{testName}-black@example.com", "RNBlack");

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

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentBag<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
