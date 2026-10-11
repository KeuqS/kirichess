using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The behavioural half of the confirmed-email play gate: that it refuses, that it stays off by
/// default, that a typo in the cutoff fails off rather than closed, and that an older account is
/// grandfathered. <see cref="PlayGatePathTests"/> is the source-scan half and needs no Docker.
///
/// Requires Docker; skips cleanly when no daemon is reachable. Fixture scaffolding follows
/// <see cref="ChallengeTests"/>, with one difference that carries the point of the whole feature: a
/// factory per test rather than one for the class, because each test sets its own cutoff. That is
/// only possible because the gate reads configuration per call instead of capturing it at
/// construction - an eagerly-read option cannot be overridden this way.
/// </summary>
public sealed class PlayGateTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    /// <summary>Any instant in the past: every account registered by these tests is created after it.</summary>
    private const string PastCutoff = "2020-01-01T00:00:00Z";

    private PostgreSqlContainer? _pg;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
        _connectionString = _pg.GetConnectionString();

        await using var factory = CreateFactory(cutoff: null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(string? cutoff) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _connectionString,
                    ["Jwt:Key"] = "play-gate-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                };
                if (cutoff is not null) settings["Auth:RequireConfirmedEmailForPlayFrom"] = cutoff;
                cfg.AddInMemoryCollection(settings);
            });
        });

    [SkippableFact]
    public async Task Gate_is_off_when_the_cutoff_is_unset()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(cutoff: null);
        using var client = factory.CreateClient();

        Authorize(client, await RegisterAsync(client, "play-gate-off@example.com", "GateOff"));

        // The default every existing deployment starts in: an unconfirmed account plays as before.
        Assert.Equal(HttpStatusCode.Created, (await CreateChallengeAsync(client)).StatusCode);
    }

    [SkippableFact]
    public async Task Unconfirmed_account_created_after_the_cutoff_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(PastCutoff);
        using var client = factory.CreateClient();

        Authorize(client, await RegisterAsync(client, "play-gate-refused@example.com", "GateRefused"));

        var challenge = await CreateChallengeAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);

        var body = await challenge.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions);
        Assert.Equal(PlayEligibilityPolicy.EmailNotConfirmedMessage, body!.Error);

        // The same refusal reaches the custom-game builder, not only the challenge path.
        var game = await client.PostAsJsonAsync("/api/games", new { Pool = "steal1" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, game.StatusCode);
    }

    [SkippableFact]
    public async Task Confirming_the_address_lifts_the_refusal_without_a_new_token()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(PastCutoff);
        using var client = factory.CreateClient();

        var me = await RegisterAsync(client, "play-gate-confirms@example.com", "GateConfirms");
        Authorize(client, me);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateChallengeAsync(client)).StatusCode);

        await MutateUserAsync(factory, me.UserId, u => u.EmailConfirmed = true);

        // The bearer token is unchanged. This is the reason the verdict is a row read rather than a
        // claim: a player who clicked the link seconds ago must not still be told to check their inbox.
        Assert.Equal(HttpStatusCode.Created, (await CreateChallengeAsync(client)).StatusCode);
    }

    [SkippableFact]
    public async Task Account_created_before_the_cutoff_is_grandfathered()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(PastCutoff);
        using var client = factory.CreateClient();

        var me = await RegisterAsync(client, "play-gate-grandfathered@example.com", "GateGrandfathered");
        Authorize(client, me);

        await MutateUserAsync(factory, me.UserId, u => u.CreatedAt = Before(PastCutoff));

        // Still unconfirmed, and still allowed: turning the gate on must not lock out players who
        // signed up before it existed and never had a reason to click the link.
        Assert.Equal(HttpStatusCode.Created, (await CreateChallengeAsync(client)).StatusCode);
    }

    [SkippableFact]
    public async Task An_unparseable_cutoff_leaves_the_gate_off()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(cutoff: "yes please");
        using var client = factory.CreateClient();

        Authorize(client, await RegisterAsync(client, "play-gate-garbage@example.com", "GateGarbage"));

        // Fails off, deliberately: a typo in a deployment variable must not wall the whole player base.
        Assert.Equal(HttpStatusCode.Created, (await CreateChallengeAsync(client)).StatusCode);
    }

    [SkippableFact]
    public async Task Email_status_reports_the_gate_verdict_not_only_the_flag()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateFactory(PastCutoff);
        using var client = factory.CreateClient();

        var me = await RegisterAsync(client, "play-gate-status@example.com", "GateStatus");
        Authorize(client, me);

        var blocked = await client.GetFromJsonAsync<EmailStatusDto>("/api/account/email", JsonOptions);
        Assert.Equal("play-gate-status@example.com", blocked!.Email);
        Assert.False(blocked.Confirmed);
        Assert.True(blocked.PlayBlocked);

        await MutateUserAsync(factory, me.UserId, u => u.CreatedAt = Before(PastCutoff));

        // Unconfirmed and NOT blocked is the grandfathered case. The banner has to tell the two apart,
        // which is why PlayBlocked is the policy's own verdict and not the client negating Confirmed.
        var grandfathered = await client.GetFromJsonAsync<EmailStatusDto>("/api/account/email", JsonOptions);
        Assert.False(grandfathered!.Confirmed);
        Assert.False(grandfathered.PlayBlocked);
    }

    private static DateTimeOffset Before(string cutoff) =>
        DateTimeOffset.Parse(cutoff, System.Globalization.CultureInfo.InvariantCulture).AddDays(-1);

    private static void Authorize(HttpClient client, AuthResult who) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private static async Task<AuthResult> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
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

    private static Task<HttpResponseMessage> CreateChallengeAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/challenges", new { Pool = "steal1", Colour = "Random" }, JsonOptions);

    private static async Task MutateUserAsync(
        WebApplicationFactory<Program> factory, Guid userId, Action<ApplicationUser> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        mutate(user);
        await db.SaveChangesAsync();
    }

    private sealed record ErrorBody(string Error);

    private sealed record AuthResult(string Token, Guid UserId);
}
