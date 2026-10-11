using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
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
/// Phase 11b.1's mail preferences: the authenticated GET/PUT pair on
/// <c>/api/account/notifications</c>, and the anonymous one-click <c>POST /api/notifications/unsubscribe</c>
/// - both its happy path and its rejection of a forged token. Requires Docker; skips cleanly when no
/// Docker daemon is reachable. Fixture scaffolding copied verbatim from <see cref="PairingNotificationTests"/>.
/// </summary>
public sealed class NotificationPreferenceTests : IAsyncLifetime
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

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "notification-preference-test-signing-key-32b",
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
    public async Task A_fresh_account_is_subscribed_to_every_category_by_default()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("notif-prefs-default@example.com", "DefaultPrefs");
        AuthorizeAs(me);

        var response = await _client!.GetAsync("/api/account/notifications");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<NotificationPreferencesDto>(JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(2, dto!.Categories.Count);
        Assert.All(dto.Categories, c => Assert.True(c.Subscribed));
    }

    [SkippableFact]
    public async Task Toggling_one_category_off_leaves_the_other_untouched()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("notif-prefs-toggle@example.com", "TogglePrefs");
        AuthorizeAs(me);

        var putResponse = await _client!.PutAsJsonAsync("/api/account/notifications", new
        {
            Category = "RematchOffered",
            Subscribed = false
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/account/notifications");
        var dto = await getResponse.Content.ReadFromJsonAsync<NotificationPreferencesDto>(JsonOptions);
        Assert.NotNull(dto);

        var rematch = Assert.Single(dto!.Categories, c => c.Category == "RematchOffered");
        Assert.False(rematch.Subscribed);
        var pairing = Assert.Single(dto.Categories, c => c.Category == "PairingFound");
        Assert.True(pairing.Subscribed);
    }

    [SkippableFact]
    public async Task Unsubscribe_link_with_a_valid_token_turns_the_category_off_with_no_auth()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("notif-prefs-unsub@example.com", "UnsubPrefs");
        var token = GenerateToken(me.UserId, NotificationCategory.PairingFound);

        var anonymousClient = _factory!.CreateClient(); // no Authorization header at all
        var response = await anonymousClient.PostAsJsonAsync("/api/notifications/unsubscribe", new
        {
            UserId = me.UserId,
            Category = "PairingFound",
            Token = token
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        AuthorizeAs(me);
        var getResponse = await _client!.GetAsync("/api/account/notifications");
        var dto = await getResponse.Content.ReadFromJsonAsync<NotificationPreferencesDto>(JsonOptions);
        var pairing = Assert.Single(dto!.Categories, c => c.Category == "PairingFound");
        Assert.False(pairing.Subscribed);
    }

    [SkippableFact]
    public async Task Unsubscribe_link_with_a_forged_token_is_rejected_and_changes_nothing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var me = await RegisterAsync("notif-prefs-forged@example.com", "ForgedPrefs");

        var anonymousClient = _factory!.CreateClient();
        var response = await anonymousClient.PostAsJsonAsync("/api/notifications/unsubscribe", new
        {
            UserId = me.UserId,
            Category = "PairingFound",
            Token = "0000000000000000000000000000000000000000000000000000000000000000"
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        AuthorizeAs(me);
        var getResponse = await _client!.GetAsync("/api/account/notifications");
        var dto = await getResponse.Content.ReadFromJsonAsync<NotificationPreferencesDto>(JsonOptions);
        var pairing = Assert.Single(dto!.Categories, c => c.Category == "PairingFound");
        Assert.True(pairing.Subscribed);
    }

    private string GenerateToken(Guid userId, NotificationCategory category)
    {
        using var scope = _factory!.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<UnsubscribeTokenService>();
        return tokens.GenerateToken(userId, category);
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
}
