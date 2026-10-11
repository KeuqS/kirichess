using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end proof of the Phase 8a.4 display-name surface: the anonymous check endpoint, the
/// self-service rename with its cooldown, and the admin scan/forced-rename pair. Requires Docker;
/// skips cleanly when no Docker daemon is reachable. Fixture scaffolding is copied verbatim from
/// <see cref="ModerationEndpointTests"/>.
/// </summary>
public sealed class DisplayNameEndpointTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "display-name-endpoint-test-signing-key-at-least-32-bytes",
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
    public async Task Check_is_anonymous()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/account/display-name/check?candidate=Bob");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.Content.ReadFromJsonAsync<VerdictDto>(JsonOptions);
        Assert.NotNull(verdict);
        Assert.True(verdict!.Allowed);
    }

    [SkippableFact]
    public async Task Check_reports_taken_for_a_lookalike()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await RegisterAsync("check-taken@example.com", "Bob");

        var response = await _client!.GetAsync("/api/account/display-name/check?candidate=B0b");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verdict = await response.Content.ReadFromJsonAsync<VerdictDto>(JsonOptions);
        Assert.NotNull(verdict);
        Assert.False(verdict!.Allowed);
        Assert.Equal("taken", verdict.Rejection);
    }

    [SkippableFact]
    public async Task Rename_requires_auth()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "Somebody" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Rename_works_and_the_first_one_is_free()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("rename-first-free@example.com", "OldName");
        AuthorizeAs(user);

        var response = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "NewName" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DisplayNameResponseDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("NewName", body!.DisplayName);
    }

    [SkippableFact]
    public async Task Cooldown_bites_on_the_second_rename_with_409()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("rename-cooldown@example.com", "OldName");
        AuthorizeAs(user);

        var first = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "NewName" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "AnotherName" }, JsonOptions);
        // 409, not 400: the request is well-formed and the name may be fine - it is the timing that
        // is refused, and Task D branches on this exact status code.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [SkippableFact]
    public async Task Rename_cannot_take_another_players_name_including_a_lookalike()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await RegisterAsync("rename-collide-owner@example.com", "Original");
        var challenger = await RegisterAsync("rename-collide-challenger@example.com", "Challenger");
        AuthorizeAs(challenger);

        // "0riginal" normalizes to the same identity key as "Original" (0 -> o).
        var response = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "0riginal" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Rename_cannot_take_a_reserved_or_profane_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("rename-reserved@example.com", "Someone");
        AuthorizeAs(user);

        var reserved = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "admin" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        var profane = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "fuckyou" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, profane.StatusCode);
    }

    [SkippableFact]
    public async Task Scan_requires_admin()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var nonAdmin = await RegisterAsync("scan-non-admin@example.com", "Regular");
        AuthorizeAs(nonAdmin);
        var forbidden = await _client!.GetAsync("/api/moderation/content/display-names");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        _client!.DefaultRequestHeaders.Authorization = null;
        var unauthorized = await _client!.GetAsync("/api/moderation/content/display-names");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [SkippableFact]
    public async Task Scan_finds_a_policy_failing_grandfathered_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("scan-finds-admin@example.com", "Admin Scan");

        // Inserted directly through the DbContext, bypassing every policy check on the write path -
        // exactly how a name from before 8a.4 could exist. The SaveChanges override keys it for us.
        Guid grandfatheredId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var grandfathered = new ApplicationUser
            {
                UserName = "grandfathered-admin-name@example.com",
                Email = "grandfathered-admin-name@example.com",
                DisplayName = "admin",
                EmailConfirmed = true
            };
            db.Users.Add(grandfathered);
            await db.SaveChangesAsync();
            grandfatheredId = grandfathered.Id;
        }

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/content/display-names?take=500&problemsOnly=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<ScanRowDto[]>(JsonOptions);
        Assert.NotNull(rows);
        Assert.Contains(rows!, r => r.UserId == grandfatheredId && r.Rejection == "reserved");
    }

    [SkippableFact]
    public async Task Forced_rename_bypasses_and_clears_the_cooldown()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("forced-rename-target@example.com", "OldName");
        var admin = await RegisterAndPromoteAsync("forced-rename-admin@example.com", "Admin Force");

        AuthorizeAs(user);
        var selfRename = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "SelfRenamed" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, selfRename.StatusCode);

        // Their cooldown is live: a second self-rename right now would 409.
        var blocked = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "ShouldBeBlocked" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        AuthorizeAs(admin);
        var forced = await _client!.PostAsJsonAsync($"/api/moderation/content/display-names/{user.UserId}",
            new { DisplayName = "ForcedName", Reason = "Moved off a flagged name." }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        // The forced rename bypassed the cooldown AND reset it, so the player can pick their own name
        // immediately - this is the whole reason the forced path exists.
        AuthorizeAs(user);
        var selfRenameAgain = await _client!.PostAsJsonAsync("/api/account/display-name",
            new { DisplayName = "PickedByPlayer" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, selfRenameAgain.StatusCode);
        var body = await selfRenameAgain.Content.ReadFromJsonAsync<DisplayNameResponseDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("PickedByPlayer", body!.DisplayName);
    }

    [SkippableFact]
    public async Task Forced_rename_still_obeys_the_policy()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("forced-rename-policy@example.com", "OldName");
        var admin = await RegisterAndPromoteAsync("forced-rename-policy-admin@example.com", "Admin Policy");

        AuthorizeAs(admin);
        var response = await _client!.PostAsJsonAsync($"/api/moderation/content/display-names/{user.UserId}",
            new { DisplayName = "admin", Reason = "Test." }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    /// <summary>
    /// Registers a user, adds them to the Admin role directly through UserManager (the same thing
    /// ModerationSeeder does at boot), then logs back in through the real endpoint so the returned
    /// token actually carries the role claim.
    ///
    /// Phase 9d.2: also enrols in 2FA and signs in through both steps - ModerationPolicies.Admin now
    /// requires the mfa claim on top of the role, so an un-enrolled admin gets 403.
    /// </summary>
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

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);

    private sealed record VerdictDto(bool Allowed, string? Rejection, string Message, string Normalized);

    private sealed record DisplayNameResponseDto(string DisplayName, DateTimeOffset ChangedAt);

    private sealed record ScanRowDto(
        Guid UserId, string DisplayName, string StoredNormalized, string RecomputedNormalized,
        bool NormalizedDrift, string? Rejection, string[] CollidesWithDisplayNames, DateTimeOffset CreatedAt);
}
