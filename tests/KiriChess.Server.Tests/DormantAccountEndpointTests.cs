using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using KiriChess.Server.Retention;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End to end proof of the Phase 8a.3 dormant-account report: the admin-role round trip, the
/// classification against real rows, tombstone exclusion, and that no response body ever carries an
/// email address (README scope decision 8). This endpoint is GET and only GET - no test here writes
/// to AspNetUsers through anything but a direct backdating write, and none deletes or anonymises
/// anything. Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class DormantAccountEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    private readonly CapturingLoggerProvider _logs = new();
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
                    ["Jwt:Key"] = "dormant-account-endpoint-test-signing-key-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
            builder.ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(_logs)));
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

        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Non_admin_gets_403()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("dormant-non-admin@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_gets_200_with_a_scan_body()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-admin-scan@example.com", "Admin Scan");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scan = await response.Content.ReadFromJsonAsync<DormantAccountScanDto>(JsonOptions);
        Assert.NotNull(scan);
        Assert.Equal(365, scan!.InactiveDays);
    }

    [SkippableFact]
    public async Task A_freshly_registered_account_is_not_reported_at_the_default_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-fresh-admin@example.com", "Admin Fresh");
        var fresh = await RegisterAsync("dormant-fresh-target@example.com", "FreshTarget");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scan = await response.Content.ReadFromJsonAsync<DormantAccountScanDto>(JsonOptions);
        Assert.NotNull(scan);
        Assert.DoesNotContain(scan!.Rows, r => r.UserId == fresh.UserId);
    }

    [SkippableFact]
    public async Task A_backdated_unconfirmed_gameless_account_is_reported_as_never_activated()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-backdated-admin@example.com", "Admin Backdated");
        var target = await RegisterAsync("dormant-backdated-target@example.com", "BackdatedTarget");
        await BackdateAsync(target.UserId, DateTimeOffset.UtcNow.AddDays(-400), emailConfirmed: false);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scan = await response.Content.ReadFromJsonAsync<DormantAccountScanDto>(JsonOptions);
        Assert.NotNull(scan);
        var row = Assert.Single(scan!.Rows, r => r.UserId == target.UserId);
        Assert.Equal(DormancyReasons.NeverActivated, row.Reason);
    }

    [SkippableFact]
    public async Task The_scan_is_logged_against_the_moderator_and_never_names_an_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-log-admin@example.com", "Admin Log");
        var target = await RegisterAsync("dormant-log-target@example.com", "LogTarget");
        await BackdateAsync(target.UserId, DateTimeOffset.UtcNow.AddDays(-400), emailConfirmed: false);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 9d.3: the report reads every live account at once, so the audit line names the moderator and
        // the scan parameters, not each account row - matching the sweep's "one line per action" rule.
        // StartsWith("Moderator ") excludes unrelated log lines that happen to carry an id, such as a
        // registration confirmation email (which embeds the recipient's id in the link).
        Assert.Contains(_logs.Messages, m => m.StartsWith("Moderator ") && m.Contains(admin.UserId.ToString()));
        Assert.DoesNotContain(_logs.Messages, m => m.StartsWith("Moderator ") && m.Contains(target.UserId.ToString()));
    }

    [SkippableFact]
    public async Task No_email_address_appears_anywhere_in_the_raw_response_body()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-noemail-admin@example.com", "Admin NoEmail");
        const string targetEmail = "dormant-noemail-target@example.com";
        var target = await RegisterAsync(targetEmail, "NoEmailTarget");
        await BackdateAsync(target.UserId, DateTimeOffset.UtcNow.AddDays(-400), emailConfirmed: false);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(targetEmail, body);
        Assert.DoesNotContain("dormant-noemail-admin@example.com", body);
    }

    [SkippableFact]
    public async Task InactiveDays_is_clamped_to_the_thirty_day_floor()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-clamp-admin@example.com", "Admin Clamp");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync("/api/moderation/retention/dormant?inactiveDays=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scan = await response.Content.ReadFromJsonAsync<DormantAccountScanDto>(JsonOptions);
        Assert.NotNull(scan);
        Assert.Equal(30, scan!.InactiveDays);
    }

    [SkippableFact]
    public async Task A_tombstoned_account_is_not_reported_and_not_in_total_accounts()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dormant-tombstone-admin@example.com", "Admin Tombstone");
        var before = await ScanAsync(admin);

        var target = await RegisterAsync("dormant-tombstone-target@example.com", "TombstoneTarget");
        await BackdateAsync(
            target.UserId, DateTimeOffset.UtcNow.AddDays(-400), emailConfirmed: false, deletedAt: DateTimeOffset.UtcNow);

        var after = await ScanAsync(admin);

        Assert.DoesNotContain(after.Rows, r => r.UserId == target.UserId);
        // The tombstoned row must not inflate the denominator either - it counts as work already done,
        // not a work-queue item (README scope decision, cross-task contract note on DormantAccountScanDto).
        Assert.Equal(before.TotalAccounts, after.TotalAccounts);
    }

    private async Task<DormantAccountScanDto> ScanAsync(AuthResult admin)
    {
        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/retention/dormant");
        response.EnsureSuccessStatusCode();
        var scan = await response.Content.ReadFromJsonAsync<DormantAccountScanDto>(JsonOptions);
        Assert.NotNull(scan);
        return scan!;
    }

    private async Task BackdateAsync(
        Guid userId, DateTimeOffset createdAt, bool emailConfirmed, DateTimeOffset? deletedAt = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.CreatedAt = createdAt;
        user.EmailConfirmed = emailConfirmed;
        user.DeletedAt = deletedAt;
        await db.SaveChangesAsync();
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
    /// token actually carries the role claim - the point is proving the round trip, not hand-minting a
    /// token that merely looks right.
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

        // Phase 9d.2: ModerationPolicies.Admin requires the mfa claim on top of the role, so the
        // fixture has to enrol and sign in through both steps or every admin route answers 403.
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
}
