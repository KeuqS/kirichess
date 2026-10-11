using KiriChess.Engine.Config;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 9e.5 pairing notification: accepting a challenge emails the challenger, since the
/// SignalR "ChallengeAccepted" push (<see cref="ChallengeEndpoints"/>) only reaches a connection
/// still watching the challenge:{token} group, and the entire point of a shareable link is that the
/// challenger is often gone by the time a friend clicks it. Requires Docker; skips cleanly when no
/// Docker daemon is reachable. Fixture scaffolding copied verbatim from <see cref="ChallengeTests"/>.
/// </summary>
public sealed class PairingNotificationTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "pairing-notification-test-signing-key-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
            // Real IEmailSender wiring needs Email:ApiKey/FromAddress (unset here), so the app
            // would fall back to LoggingEmailSender anyway - replaced with a capturing stub so the
            // tests can assert on what was actually handed off, the way a real provider would see it.
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
    public async Task Accepting_a_challenge_emails_the_challenger_when_their_address_is_confirmed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("pairing-notify-challenger@example.com", "NotifyChallenger");
        var friend = await RegisterAsync("pairing-notify-friend@example.com", "NotifyFriend");
        await ConfirmEmailAsync(challenger.UserId);

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        AuthorizeAs(friend);
        var accepted = await AcceptAsync(challenge.Token);

        // RegisterAsync sends a confirmation email per account (AuthEndpoints), so the sink holds
        // those too. Assert about the pairing email specifically, which is what this test is for.
        var sent = Assert.Single(_email!.Sent, m => m.Subject == EmailTemplates.PairingFoundSubject);
        Assert.Equal("pairing-notify-challenger@example.com", sent.ToEmail);
        Assert.Equal(EmailTemplates.PairingFoundSubject, sent.Subject);
        Assert.Contains($"/game/{accepted.GameId}", sent.TextBody);
        // The mail carries the pool's display name, not its storage key: "casual steal1 game" is not
        // something to put in front of a person. KiriChessConfig.PoolLabel is what makes the difference.
        Assert.Contains("casual " + KiriChessConfig.PoolLabel("steal1"), sent.TextBody);
        Assert.DoesNotContain("steal1", sent.TextBody);
        // Phase 11b.1: every governed mail carries a working opt-out, this one included.
        Assert.Contains("/unsubscribe?", sent.TextBody);
    }

    [SkippableFact]
    public async Task Accepting_a_challenge_sends_no_email_when_the_challenger_address_is_unconfirmed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // RegisterAsync leaves EmailConfirmed false (Phase 8a: confirmation is a separate step) -
        // this is the default state for every account this test suite creates elsewhere, so this
        // case is the common one, not the edge case.
        var challenger = await RegisterAsync("pairing-notify-unconfirmed@example.com", "UnconfirmedChallenger");
        var friend = await RegisterAsync("pairing-notify-unconfirmed-friend@example.com", "UnconfirmedFriend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        AuthorizeAs(friend);
        await AcceptAsync(challenge.Token);

        // Same reason as above: the two registration confirmations are expected, a pairing email is not.
        Assert.DoesNotContain(_email!.Sent, m => m.Subject == EmailTemplates.PairingFoundSubject);
    }

    [SkippableFact]
    public async Task Accepting_a_challenge_sends_no_email_when_the_challenger_has_unsubscribed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("pairing-notify-unsubscribed@example.com", "UnsubscribedChallenger");
        var friend = await RegisterAsync("pairing-notify-unsubscribed-friend@example.com", "UnsubscribedFriend");
        await ConfirmEmailAsync(challenger.UserId);

        using (var scope = _factory!.Services.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
            await preferences.SetSubscribedAsync(challenger.UserId, NotificationCategory.PairingFound, subscribed: false);
        }

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        AuthorizeAs(friend);
        await AcceptAsync(challenge.Token);

        // A confirmed address is not enough on its own (Phase 11b.1): the preference gate runs too.
        Assert.DoesNotContain(_email!.Sent, m => m.Subject == EmailTemplates.PairingFoundSubject);
    }

    private async Task ConfirmEmailAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
    }

    private async Task<ChallengeAcceptedDto> AcceptAsync(string token)
    {
        var response = await _client!.PostAsync($"/api/challenges/{token}/accept", content: null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!;
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

    private async Task<ChallengeDto> CreateChallengeAsync(string pool, string colour)
    {
        var response = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = pool,
            Colour = colour
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!;
    }

    private void AuthorizeAs(AuthResult auth) =>
        _client!.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);

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
