using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End to end proof of the Phase 8.5 moderation surface: the admin-role round trip through a real
/// login, the report queue and resolution, sanction issuance/lift, ban enforcement at login and
/// refresh, and that <c>ModeratorNote</c> never reaches a non-admin route. Requires Docker; skips
/// cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class ModerationEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";
    private const string AdminNotifyEmail = "ops-notify@example.com";

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
                    ["Jwt:Key"] = "moderation-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    // S-3-5: harmless for every other test in this class - ModerationSeeder just skips
                    // an AdminEmails entry with no matching account (S-3-2), which this address never
                    // has. Present so Filing_a_report_notifies_the_configured_admin_address has an
                    // address to notify.
                    ["Moderation:AdminEmails:0"] = AdminNotifyEmail
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
    public async Task Non_admin_gets_403_on_reports()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("non-admin-reports@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync("/api/moderation/reports");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unauthenticated_gets_401_on_reports()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/moderation/reports");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_sees_an_open_report_filed_by_someone_else()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("report-queue-admin@example.com", "Admin One");
        var reporter = await RegisterAsync("report-queue-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("report-queue-reported@example.com", "Reported");

        AuthorizeAs(reporter);
        var fileResponse = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = "Test."
        }, JsonOptions);
        fileResponse.EnsureSuccessStatusCode();

        AuthorizeAs(admin);
        var listResponse = await _client!.GetAsync("/api/moderation/reports");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var rows = await listResponse.Content.ReadFromJsonAsync<ModerationReportDto[]>(JsonOptions);
        Assert.NotNull(rows);
        Assert.Contains(rows!, r => r.ReportedUserId == reported.UserId && r.Status == "Open");
    }

    /// <summary>
    /// S-3-5: filing a report used to be entirely pull-only - nothing told the moderator a queue
    /// existed to drain. <c>LoggingEmailSender</c> (the sender wired in tests, since no Resend key is
    /// configured) logs the plain-text body of every mail it "sends", so the notification is
    /// observable through <c>_logs</c> the same way the dossier-read audit line is, without a real
    /// inbox.
    /// </summary>
    [SkippableFact]
    public async Task Filing_a_report_notifies_the_configured_admin_address()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("notify-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("notify-reported@example.com", "Reported");

        AuthorizeAs(reporter);
        var fileResponse = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = "Confidential detail that must not appear in the admin mail."
        }, JsonOptions);
        fileResponse.EnsureSuccessStatusCode();

        Assert.Contains(_logs.Messages, m =>
            m.Contains(AdminNotifyEmail) &&
            m.Contains("New report filed on KiriChess") &&
            m.Contains(reported.UserId.ToString()));

        // S-1-10's concern applied to this new surface: Details is reporter-written free text and
        // must not be forwarded into the admin's mail client.
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("Confidential detail"));
    }

    /// <summary>
    /// S-3-4: the repeat-offender signal used to be <c>g.Count()</c> over open report rows, which one
    /// account could inflate on its own by filing several reports against the same target for
    /// different games (ReportEndpoints' own duplicate suppression only collapses a repeat
    /// (ReporterId, ReportedUserId, GameId) - a different GameId is a different row). Two rows
    /// inserted directly here stand in for that: same reporter, same target, two report rows (the API
    /// path is exercised separately by <see cref="Admin_sees_an_open_report_filed_by_someone_else"/>,
    /// this test is about the aggregation, not the intake). The fixed query counts DISTINCT reporters,
    /// so this must read 1, and a genuinely different reporter filing a third row must bring it to 2.
    /// </summary>
    [SkippableFact]
    public async Task Repeat_reports_from_one_account_do_not_inflate_the_signal_but_a_second_account_does()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("repeat-offender-admin@example.com", "Admin Repeat");
        var reporter = await RegisterAsync("repeat-offender-reporter@example.com", "Reporter One");
        var otherReporter = await RegisterAsync("repeat-offender-reporter-two@example.com", "Reporter Two");
        var reported = await RegisterAsync("repeat-offender-reported@example.com", "Reported");

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            // GameId left null on both: the FK constraint on Report.GameId means a non-null value
            // would need a real Game row, and duplicate suppression (an app-level check in
            // ReportEndpoints, not a DB constraint) is bypassed entirely by inserting directly - the
            // point here is two open rows from one reporter existing at all, however they got there.
            db.Reports.AddRange(
                new Report { ReporterId = reporter.UserId, ReportedUserId = reported.UserId, Reason = ReportReason.Abuse },
                new Report { ReporterId = reporter.UserId, ReportedUserId = reported.UserId, Reason = ReportReason.Stalling });
            await db.SaveChangesAsync();
        }

        AuthorizeAs(admin);
        var firstCheck = await _client!.GetAsync("/api/moderation/reports");
        var firstRows = await firstCheck.Content.ReadFromJsonAsync<ModerationReportDto[]>(JsonOptions);
        // Two report ROWS exist for this target (both inserted above), which is exactly the case
        // that used to inflate the old row-count signal to 2. Both rows must agree the signal is 1,
        // one reporter, because that is what the fixed query means.
        var rowsForTarget = firstRows!.Where(r => r.ReportedUserId == reported.UserId).ToArray();
        Assert.Equal(2, rowsForTarget.Length);
        Assert.All(rowsForTarget, r => Assert.Equal(1, r.OpenReportsAgainstReportedUser));

        AuthorizeAs(otherReporter);
        var secondFile = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = (string?)null
        }, JsonOptions);
        secondFile.EnsureSuccessStatusCode();

        AuthorizeAs(admin);
        var secondCheck = await _client!.GetAsync("/api/moderation/reports");
        var secondRows = await secondCheck.Content.ReadFromJsonAsync<ModerationReportDto[]>(JsonOptions);
        var rowsForTargetAfter = secondRows!.Where(r => r.ReportedUserId == reported.UserId).ToArray();
        // Three report rows now (two from the first reporter, one from the second), but only two
        // distinct reporters - the signal must read 2, not 3.
        Assert.Equal(3, rowsForTargetAfter.Length);
        Assert.All(rowsForTargetAfter, r => Assert.Equal(2, r.OpenReportsAgainstReportedUser));
    }

    [SkippableFact]
    public async Task Dossier_read_returns_the_target_and_is_logged_against_the_moderator()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("dossier-admin@example.com", "Admin Dossier");
        var target = await RegisterAsync("dossier-target@example.com", "Target");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/users/{target.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dossier = await response.Content.ReadFromJsonAsync<ModerationUserDto>(JsonOptions);
        Assert.NotNull(dossier);
        Assert.Equal(target.UserId, dossier!.UserId);

        // 9d.3: the read itself must leave a trace naming both who looked and who was looked at.
        // StartsWith("Moderator ") excludes unrelated log lines that happen to carry the admin's own
        // id, such as their registration confirmation email (which embeds it in the link).
        Assert.Contains(_logs.Messages, m =>
            m.StartsWith("Moderator ") && m.Contains(admin.UserId.ToString()) && m.Contains(target.UserId.ToString()));
    }

    [SkippableFact]
    public async Task Grace_disconnects_are_stored_per_seat_and_shown_only_for_that_player()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // S-2-6. The arm is a column on the game, summed onto the dossier for that seat alone.
        // A solo game must not be counted twice, and an export must not carry the opponent's count.
        var admin = await RegisterAndPromoteAsync("grace-admin@example.com", "Grace Admin");
        var white = await RegisterAsync("grace-white@example.com", "Grace White");
        var black = await RegisterAsync("grace-black@example.com", "Grace Black");
        var solo = await RegisterAsync("grace-solo@example.com", "Grace Solo");

        var versusId = Guid.NewGuid();
        var soloId = Guid.NewGuid();
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            db.Games.Add(NewGame(versusId, white.UserId, black.UserId));
            db.Games.Add(NewGame(soloId, solo.UserId, solo.UserId));
            await db.SaveChangesAsync();
        }

        var store = new GraceDisconnectStore(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GraceDisconnectStore>.Instance);
        await store.RecordAsync(versusId, white.UserId);
        await store.RecordAsync(versusId, white.UserId);
        await store.RecordAsync(versusId, black.UserId);
        await store.RecordAsync(soloId, solo.UserId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var versus = await db.Games.SingleAsync(g => g.Id == versusId);
            Assert.Equal(2, versus.WhiteDisconnectCount);
            Assert.Equal(1, versus.BlackDisconnectCount);
            var soloGame = await db.Games.SingleAsync(g => g.Id == soloId);
            Assert.Equal(1, soloGame.WhiteDisconnectCount);
            Assert.Equal(0, soloGame.BlackDisconnectCount);
        }

        AuthorizeAs(admin);
        var client = _client!;
        var whiteDossier = await client.GetFromJsonAsync<ModerationUserDto>(
            $"/api/moderation/users/{white.UserId}", JsonOptions);
        var blackDossier = await client.GetFromJsonAsync<ModerationUserDto>(
            $"/api/moderation/users/{black.UserId}", JsonOptions);
        var soloDossier = await client.GetFromJsonAsync<ModerationUserDto>(
            $"/api/moderation/users/{solo.UserId}", JsonOptions);
        Assert.Equal(2, whiteDossier!.GraceDisconnects);
        Assert.Equal(1, blackDossier!.GraceDisconnects);
        Assert.Equal(1, soloDossier!.GraceDisconnects);

        AuthorizeAs(white);
        Assert.Equal(2, (await ExportDisconnectsAsync()).Single(g => g.GameId == versusId).DisconnectCount);
        AuthorizeAs(black);
        Assert.Equal(1, (await ExportDisconnectsAsync()).Single(g => g.GameId == versusId).DisconnectCount);
        AuthorizeAs(solo);
        Assert.Equal(1, (await ExportDisconnectsAsync()).Single(g => g.GameId == soloId).DisconnectCount);
    }

    [SkippableFact]
    public async Task Resolving_a_report_stamps_reviewer_and_flips_status_resolving_twice_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("resolve-admin@example.com", "Admin Two");
        var reporter = await RegisterAsync("resolve-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("resolve-reported@example.com", "Reported");

        AuthorizeAs(reporter);
        var fileResponse = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = (string?)null
        }, JsonOptions);
        var filed = await fileResponse.Content.ReadFromJsonAsync<FiledReportDto>(JsonOptions);
        Assert.NotNull(filed);

        AuthorizeAs(admin);
        var resolveResponse = await _client!.PostAsJsonAsync($"/api/moderation/reports/{filed!.Id}/resolve", new
        {
            Status = "Dismissed",
            ModeratorNote = "Not enough evidence."
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<ModerationReportDto>(JsonOptions);
        Assert.NotNull(resolved);
        Assert.Equal("Dismissed", resolved!.Status);
        Assert.NotNull(resolved.ReviewedAt);
        Assert.NotNull(resolved.ReviewedByUserId);

        var secondResolve = await _client!.PostAsJsonAsync($"/api/moderation/reports/{filed.Id}/resolve", new
        {
            Status = "Reviewed",
            ModeratorNote = (string?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, secondResolve.StatusCode);
    }

    [SkippableFact]
    public async Task Account_ban_blocks_login_and_refresh_and_lifting_restores_login()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("ban-flow-admin@example.com", "Admin Three");
        var target = await RegisterAsync("ban-flow-target@example.com", "Target");

        AuthorizeAs(admin);
        var issueResponse = await _client!.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = target.UserId,
            Type = "AccountBan",
            Reason = "Cheating.",
            ModeratorNote = "Confirmed by replay review.",
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, issueResponse.StatusCode);
        var sanction = await issueResponse.Content.ReadFromJsonAsync<SanctionResultDto>(JsonOptions);
        Assert.NotNull(sanction);

        var lockedLogin = await _client!.PostAsJsonAsync("/api/auth/login", new
        {
            Email = "ban-flow-target@example.com",
            Password
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, lockedLogin.StatusCode);

        // Issuing the AccountBan must revoke the target's existing refresh tokens so the ban bites
        // now, not whenever their live access token happens to expire.
        var refreshAfterBan = await _client!.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = target.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterBan.StatusCode);

        AuthorizeAs(admin);
        var liftResponse = await _client!.PostAsJsonAsync($"/api/moderation/sanctions/{sanction!.Id}/lift",
            new { Reason = "Appeal upheld." }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);

        var unlockedLogin = await _client!.PostAsJsonAsync("/api/auth/login", new
        {
            Email = "ban-flow-target@example.com",
            Password
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, unlockedLogin.StatusCode);
    }

    [SkippableFact]
    public async Task My_sanctions_never_exposes_the_moderator_note()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("note-leak-admin@example.com", "Admin Four");
        var target = await RegisterAsync("note-leak-target@example.com", "Target");

        AuthorizeAs(admin);
        const string secretNote = "TOP-SECRET-MODERATOR-ONLY-NOTE";
        var issueResponse = await _client!.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = target.UserId,
            Type = "Warning",
            Reason = "Please slow down between moves.",
            ModeratorNote = secretNote,
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, issueResponse.StatusCode);

        AuthorizeAs(target);
        var mineResponse = await _client!.GetAsync("/api/moderation/my-sanctions");
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);
        var body = await mineResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(secretNote, body);
        var rows = JsonSerializer.Deserialize<MySanctionResultDto[]>(body, JsonOptions);
        Assert.NotNull(rows);
        Assert.Single(rows!);
        Assert.Equal("Warning", rows![0].Type);
    }

    [SkippableFact]
    public async Task Moderator_cannot_sanction_another_moderator()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var adminOne = await RegisterAndPromoteAsync("dual-admin-one@example.com", "Admin Five");
        var adminTwo = await RegisterAndPromoteAsync("dual-admin-two@example.com", "Admin Six");

        AuthorizeAs(adminOne);
        var response = await _client!.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = adminTwo.UserId,
            Type = "Warning",
            Reason = "Test.",
            ModeratorNote = (string?)null,
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Non_admin_gets_403_on_admin_export()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("non-admin-export@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync($"/api/moderation/users/{user.UserId}/export");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_export_of_another_account_is_an_attachment_and_is_logged()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("gdpr-export-admin@example.com", "Admin Export");
        var target = await RegisterAsync("gdpr-export-target@example.com", "Export Target");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/users/{target.UserId}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("gdpr-export-target@example.com", body);

        Assert.Contains(_logs.Messages, m =>
            m.Contains("exported account") && m.Contains(admin.UserId.ToString()) && m.Contains(target.UserId.ToString()));
    }

    [SkippableFact]
    public async Task Admin_erase_requires_the_exact_acknowledgement()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("gdpr-ack-admin@example.com", "Admin Ack");
        var target = await RegisterAsync("gdpr-ack-target@example.com", "Ack Target");

        AuthorizeAs(admin);
        var response = await _client!.PostAsJsonAsync(
            $"/api/moderation/users/{target.UserId}/erase",
            new { Acknowledgement = "DELETE MY ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == target.UserId);
        Assert.Null(row.DeletedAt);
        Assert.Equal("gdpr-ack-target@example.com", row.Email);
    }

    [SkippableFact]
    public async Task Admin_cannot_erase_self_or_another_admin()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var adminOne = await RegisterAndPromoteAsync("gdpr-self-admin@example.com", "Admin Self");
        var adminTwo = await RegisterAndPromoteAsync("gdpr-peer-admin@example.com", "Admin Peer");

        AuthorizeAs(adminOne);
        var selfResponse = await _client!.PostAsJsonAsync(
            $"/api/moderation/users/{adminOne.UserId}/erase",
            new { Acknowledgement = "ERASE THIS ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, selfResponse.StatusCode);

        var peerResponse = await _client!.PostAsJsonAsync(
            $"/api/moderation/users/{adminTwo.UserId}/erase",
            new { Acknowledgement = "ERASE THIS ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, peerResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_erase_anonymises_a_banned_account_and_keeps_the_sanction()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("gdpr-erase-admin@example.com", "Admin Erase");
        var target = await RegisterAsync("gdpr-erase-target@example.com", "Erase Target");

        AuthorizeAs(admin);
        var issueResponse = await _client!.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = target.UserId,
            Type = "AccountBan",
            Reason = "Test ban for GDPR erase.",
            ModeratorNote = (string?)null,
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, issueResponse.StatusCode);

        var login = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "gdpr-erase-target@example.com", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        AuthorizeAs(admin);
        var eraseResponse = await _client!.PostAsJsonAsync(
            $"/api/moderation/users/{target.UserId}/erase",
            new { Acknowledgement = "ERASE THIS ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, eraseResponse.StatusCode);

        Assert.Contains(_logs.Messages, m =>
            m.Contains("erased account") && m.Contains(admin.UserId.ToString()) && m.Contains(target.UserId.ToString()));

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == target.UserId);
        Assert.Null(row.Email);
        Assert.Equal("Deleted account", row.DisplayName);
        Assert.NotNull(row.DeletedAt);
        Assert.True(await db.UserSanctions.AnyAsync(s => s.UserId == target.UserId && s.LiftedAt == null));

        var secondExport = await _client!.GetAsync($"/api/moderation/users/{target.UserId}/export");
        Assert.Equal(HttpStatusCode.NotFound, secondExport.StatusCode);
    }

    [SkippableFact]
    public async Task Reports_mine_keeps_its_original_shape()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("shape-guard-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("shape-guard-reported@example.com", "Reported");
        AuthorizeAs(reporter);

        var fileResponse = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Other",
            Details = (string?)null
        }, JsonOptions);
        fileResponse.EnsureSuccessStatusCode();

        var mineResponse = await _client!.GetAsync("/api/reports/mine");
        mineResponse.EnsureSuccessStatusCode();
        var rows = await mineResponse.Content.ReadFromJsonAsync<FiledReportDto[]>(JsonOptions);

        Assert.NotNull(rows);
        Assert.Contains(rows!, r => r.ReportedUserId == reported.UserId);
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private static Game NewGame(Guid id, Guid whiteId, Guid blackId) => new()
    {
        Id = id,
        WhitePlayerId = whiteId,
        BlackPlayerId = blackId,
        Result = GameResult.Ongoing,
        Termination = TerminationReason.None,
        RatingPool = "steal1",
        IsRated = true,
        StartFen = string.Empty,
        ConfigSnapshot = new KiriChessConfig(),
        Moves = [],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<ExportGameDisconnect[]> ExportDisconnectsAsync()
    {
        var response = await _client!.GetAsync("/api/account/export");
        response.EnsureSuccessStatusCode();
        var file = await response.Content.ReadFromJsonAsync<ExportFile>(JsonOptions);
        Assert.NotNull(file);
        return file!.Games;
    }

    private sealed record ExportGameDisconnect(Guid GameId, int DisconnectCount);
    private sealed record ExportFile(ExportGameDisconnect[] Games);

    /// <summary>
    /// S-5-1's sibling, S-3-2. Registration issues tokens immediately and never proves the
    /// registrant controls the address, so before this check whoever registered a listed address
    /// FIRST got Admin on the next boot, owned it or not. The seeder's own comment treated "no
    /// account yet" as the benign case and never distinguished it from "an account exists and the
    /// address was never verified", which is the takeover window.
    /// </summary>
    [SkippableFact]
    public async Task Seeder_refuses_Admin_to_a_listed_address_whose_account_never_confirmed_it()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string email = "seeder-unverified-admin@example.com";
        var squatter = await RegisterAsync(email, "Squatter");

        using var scope = _factory!.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ModerationSeederTest");

        var user = await users.FindByIdAsync(squatter.UserId.ToString());
        Assert.NotNull(user);
        Assert.False(user!.EmailConfirmed, "registration must not confirm the address by itself, or this finding cannot exist");

        await ModerationSeeder.SeedAsync(roles, users, new ModerationOptions([email]), logger);

        Assert.False(await users.IsInRoleAsync(user, ModerationRoles.Admin));
        Assert.Contains(_logs.Messages, m => m.Contains("refusing the Admin role"));

        // The other half: this is a refusal to grant, not a permanent lockout. Confirming the
        // address is the owner's path in, and it is the one that must still work, otherwise a
        // restore into an empty database would have no way to produce a first moderator.
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);

        await ModerationSeeder.SeedAsync(roles, users, new ModerationOptions([email]), logger);
        Assert.True(await users.IsInRoleAsync(user, ModerationRoles.Admin));
    }

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
    ///
    /// Phase 9d.2: the fixture also enrols in 2FA and signs in through both steps, because
    /// ModerationPolicies.Admin now requires the mfa claim on top of the role - an un-enrolled admin
    /// gets 403 from every route this class exercises. That soft-lock has its own coverage in
    /// TwoFactorTests; here it is only setup.
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

    private sealed record FiledReportDto(
        Guid Id, Guid ReportedUserId, Guid? GameId, string Reason, string? Details, string Status, DateTimeOffset CreatedAt);

    private sealed record ModerationReportDto(
        Guid Id, Guid ReporterId, string ReporterDisplayName, Guid ReportedUserId, string ReportedUserDisplayName,
        Guid? GameId, string Reason, string? Details, string Status, string? ModeratorNote,
        Guid? ReviewedByUserId, DateTimeOffset? ReviewedAt, DateTimeOffset CreatedAt, int OpenReportsAgainstReportedUser);

    private sealed record SanctionResultDto(
        Guid Id, Guid UserId, string UserDisplayName, string Type, string Reason, string? ModeratorNote,
        Guid IssuedByUserId, string IssuedByDisplayName, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt,
        DateTimeOffset? LiftedAt, Guid? LiftedByUserId, string? LiftReason, Guid? ReportId, bool IsActive);

    private sealed record MySanctionResultDto(string Type, string Reason, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt);
}
