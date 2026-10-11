using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Games;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 10c.4: the admin round trip (GET/PUT /api/moderation/kill-switches) and that each of the
/// four gated call sites actually honours the switch - registration and the seek list (REST, a 503
/// with the stated reason) and pairing/spectating (the hub, a <c>HubException</c> the client already
/// surfaces, see <c>RatedPoolsGrid</c>/the watch page). Requires Docker; skips cleanly when no Docker
/// daemon is reachable.
/// </summary>
public sealed class KillSwitchTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "kill-switch-test-signing-key-at-least-32-bytes",
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
    public async Task Anonymous_gets_401()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/moderation/kill-switches/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Non_admin_gets_403()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("kill-switch-non-admin@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync("/api/moderation/kill-switches/");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task All_four_switches_start_enabled()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("kill-switch-defaults-admin@example.com", "Admin Defaults");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync("/api/moderation/kill-switches/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var switches = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(JsonOptions);
        Assert.NotNull(switches);
        foreach (var which in Enum.GetValues<KillSwitch>())
            Assert.True(switches![which.ToString()], $"{which} should start enabled.");
    }

    [SkippableFact]
    public async Task Admin_can_disable_and_reenable_a_switch_and_it_is_attributed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("kill-switch-toggle-admin@example.com", "Admin Toggle");
        AuthorizeAs(admin);

        var off = await _client!.PutAsJsonAsync(
            "/api/moderation/kill-switches/SeekList", new { Enabled = false }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        var afterOff = await GetSwitchesAsync();
        Assert.False(afterOff[nameof(KillSwitch.SeekList)]);
        Assert.True(afterOff[nameof(KillSwitch.Registration)]); // untouched

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var row = await db.KillSwitches.SingleAsync(k => k.Name == nameof(KillSwitch.SeekList));
            Assert.Equal(admin.UserId, row.UpdatedByUserId);
        }

        var on = await _client!.PutAsJsonAsync(
            "/api/moderation/kill-switches/SeekList", new { Enabled = true }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);

        var afterOn = await GetSwitchesAsync();
        Assert.True(afterOn[nameof(KillSwitch.SeekList)]);
    }

    [SkippableFact]
    public async Task Unknown_switch_name_is_404()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("kill-switch-unknown-admin@example.com", "Admin Unknown");
        AuthorizeAs(admin);

        var response = await _client!.PutAsJsonAsync(
            "/api/moderation/kill-switches/NotARealSwitch", new { Enabled = false }, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Disabling_registration_refuses_a_new_signup_with_a_stated_reason()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await SetSwitchDirectlyAsync(KillSwitch.Registration, enabled: false);

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "kill-switch-blocked-register@example.com",
            Password,
            DisplayName = "Blocked",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [SkippableFact]
    public async Task Disabling_the_seek_list_returns_503_instead_of_the_normal_list()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await SetSwitchDirectlyAsync(KillSwitch.SeekList, enabled: false);

        var response = await _client!.GetAsync("/api/seeks");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [SkippableFact]
    public async Task Disabling_pairing_refuses_JoinQueue_over_the_hub()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var player = await RegisterAsync("kill-switch-pairing-player@example.com", "Pairing Player");
        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: false);

        await using var conn = BuildGameHubConnection(player.Token);
        await conn.StartAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => conn.InvokeAsync("JoinQueue", "bullet"));
        Assert.Contains("Matchmaking is temporarily paused", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Disabling_spectating_refuses_JoinAsSpectator_over_the_hub()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await SetSwitchDirectlyAsync(KillSwitch.Spectating, enabled: false);

        // Anonymous, same as a real spectator: JoinAsSpectator is the one hub method with no token.
        await using var conn = BuildGameHubConnection(accessToken: null);
        await conn.StartAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => conn.InvokeAsync("JoinAsSpectator", Guid.NewGuid().ToString()));
        Assert.Contains("Spectating is temporarily disabled", ex.Message, StringComparison.Ordinal);
    }

    private HubConnection BuildGameHubConnection(string? accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                // Same recipe as TwoInstanceGameTests: TestServer has no real socket, so long polling
                // over the factory's own handler is what lets a HubConnection reach this host at all.
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
            })
            .Build();

    private async Task SetSwitchDirectlyAsync(KillSwitch which, bool enabled)
    {
        using var scope = _factory!.Services.CreateScope();
        var killSwitches = scope.ServiceProvider.GetRequiredService<KillSwitchService>();
        await killSwitches.SetEnabledAsync(which, enabled, Guid.NewGuid());
    }

    private async Task<Dictionary<string, bool>> GetSwitchesAsync()
    {
        var response = await _client!.GetAsync("/api/moderation/kill-switches/");
        response.EnsureSuccessStatusCode();
        var switches = await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(JsonOptions);
        Assert.NotNull(switches);
        return switches!;
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password,
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    /// <summary>Same recipe as DormantAccountEndpointTests: role grant plus the real TOTP round trip, since ModerationPolicies.Admin also requires the mfa claim.</summary>
    private async Task<AuthResult> RegisterAndPromoteAsync(string email, string displayName)
    {
        var registered = await RegisterAsync(email, displayName);

        using (var scope = _factory!.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roles.RoleExistsAsync(ModerationRoles.Admin))
                await roles.CreateAsync(new IdentityRole<Guid>(ModerationRoles.Admin));
            var user = await users.FindByIdAsync(registered.UserId.ToString());
            await users.AddToRoleAsync(user!, ModerationRoles.Admin);
        }

        var sharedKey = await TotpTestHelper.EnableTwoFactorAsync(_factory!.Services, registered.UserId);

        var loginResponse = await TotpTestHelper.LoginWithTwoFactorAsync(
            _client!, email, Password, sharedKey, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
        var relogged = await loginResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(relogged);
        return relogged!;
    }

    // ---------------------------------------------------------------------------------------
    // S-5-1. The switch used to be enforced in exactly one place, GameHub.JoinQueue, while
    // KillSwitch.cs and the 10c.4 roadmap entry both promised "nobody enters a new game". Four
    // other paths kept inserting rows. The gate now lives in GameRegistry.CreateAsync, which is
    // the single function all five funnel through and the one that does the insert.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The gate itself, which is what makes the other four paths safe. Asserted directly on the
    /// registry rather than only through an endpoint: this is the claim that no creation path can
    /// bypass it, including one added after this test was written, and that claim is about the
    /// function, not about any particular caller.
    /// </summary>
    [SkippableFact]
    public async Task Pairing_off_refuses_creation_at_the_registry_every_path_funnels_through()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var registry = _factory!.Services.GetRequiredService<GameRegistry>();
        var config = KiriChessConfig.CreateSteal1();

        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: false);
        await Assert.ThrowsAsync<GameCreationPausedException>(
            () => registry.CreateAsync(config, Guid.NewGuid(), Guid.NewGuid(), pool: null, rated: false));

        // And the switch is a pause, not a one-way door: turning it back on restores creation.
        // Without this half the test would still pass if the gate were accidentally unconditional.
        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: true);
        var player = await RegisterAsync("kill-switch-registry-gate@example.com", "Registry Gate");
        var session = await registry.CreateAsync(
            config, player.UserId, player.UserId, pool: null, rated: false);
        Assert.NotEqual(Guid.Empty, session.Id);
    }

    [SkippableFact]
    public async Task Disabling_pairing_returns_503_from_POST_api_games()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var player = await RegisterAsync("kill-switch-create-game@example.com", "Create Game");
        AuthorizeAs(player);
        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: false);

        var response = await _client!.PostAsJsonAsync(
            "/api/games/", new { OpponentId = (Guid?)null, Preset = "steal1", Rated = false }, JsonOptions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("temporarily paused", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering half, which is the part neither S-5-1 nor the fix's first draft covers. The
    /// accept path stamps AcceptedAt BEFORE calling CreateAsync, so a refusal that happened only at
    /// the registry would leave the challenge consumed with no game to show for it: the player
    /// loses their link to an incident they had nothing to do with. The check therefore runs before
    /// the claim, and this asserts the challenge is still open afterwards.
    /// </summary>
    [SkippableFact]
    public async Task Disabling_pairing_refuses_a_challenge_accept_without_burning_the_challenge()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("kill-switch-challenger@example.com", "Challenger");
        var accepter = await RegisterAsync("kill-switch-accepter@example.com", "Accepter");

        AuthorizeAs(challenger);
        var createResponse = await _client!.PostAsJsonAsync(
            "/api/challenges/", new { Pool = "steal1", Colour = "white" }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var token = created.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: false);

        AuthorizeAs(accepter);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{token}/accept", content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, acceptResponse.StatusCode);

        // The claim never happened, so the link still works once the incident is over.
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var row = await db.Challenges.AsNoTracking().SingleAsync(c => c.Token == token);
            Assert.Null(row.AcceptedAt);
            Assert.Null(row.GameId);
        }

        await SetSwitchDirectlyAsync(KillSwitch.Pairing, enabled: true);
        var retry = await _client.PostAsync($"/api/challenges/{token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);
}
