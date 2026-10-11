using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using KiriChess.Server.Moderation.Detection;
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
/// Wiring proof for the Phase 8.5 Task B routes: auth gating, the 404 on an unknown user, the
/// opponent-rating pairing (the one bug this class exists to catch), and that the sweep respects
/// <c>take</c>. The maths itself is covered by <see cref="SandbaggingAnalyzerTests"/> without a
/// database - this file stays thin on purpose. Requires Docker; skips cleanly when no Docker daemon
/// is reachable.
/// </summary>
public sealed class SandbaggingEndpointTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "sandbagging-endpoint-test-signing-key-at-least-32-bytes",
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
    public async Task Non_admin_gets_403()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("sandbagging-non-admin@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unauthenticated_gets_401()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_unknown_user_returns_404()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-404-admin@example.com", "AdminUser");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_sees_one_report_per_pool_for_a_seeded_player()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-report-admin@example.com", "AdminUser");
        var subject = await RegisterAsync("sandbagging-report-subject@example.com", "Subject");
        var opponent = await RegisterAsync("sandbagging-report-opponent@example.com", "Opponent");

        var endedAt = DateTimeOffset.UtcNow.AddDays(-1);
        for (int i = 0; i < 20; i++)
        {
            var result = i % 2 == 0 ? GameResult.WhiteWins : GameResult.BlackWins;
            await SeedRatedGameAsync(
                subject.UserId, opponent.UserId, result, TerminationReason.Checkmate, "steal1", plies: 40,
                endedAt.AddHours(i), subjectBefore: 1500, subjectAfter: 1500, opponentBefore: 1500, opponentAfter: 1500);
        }

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{subject.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reports = await response.Content.ReadFromJsonAsync<DetectionReportDto[]>(JsonOptions);
        Assert.NotNull(reports);
        var report = Assert.Single(reports!);
        Assert.Equal("sandbagging", report.Detector);
        Assert.Equal("steal1", report.Pool);
        Assert.Equal(20, report.SampleSize);
    }

    [SkippableFact]
    public async Task Opponent_rating_is_read_from_the_sibling_row_not_defaulted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-pairing-admin@example.com", "AdminUser");
        var subject = await RegisterAsync("sandbagging-pairing-subject@example.com", "Subject");
        var weakOpponent = await RegisterAsync("sandbagging-pairing-weak@example.com", "Weak Opponent");
        var evenOpponent = await RegisterAsync("sandbagging-pairing-even@example.com", "Even Opponent");

        var endedAt = DateTimeOffset.UtcNow.AddDays(-1);

        // 12 losses to an opponent whose sibling RatingHistory row is 300 points below the subject's -
        // a 150+ gap, so this must land in "losses-to-weaker" if (and only if) the opponent's real
        // ValueBefore is read from their own row rather than defaulted to the subject's 1500.
        for (int i = 0; i < 12; i++)
        {
            await SeedRatedGameAsync(
                subject.UserId, weakOpponent.UserId, GameResult.BlackWins, TerminationReason.Checkmate, "steal1", plies: 40,
                endedAt.AddHours(i), subjectBefore: 1500, subjectAfter: 1480, opponentBefore: 1200, opponentAfter: 1220);
        }
        // 8 wins against an opponent at the same rating - decisive filler so the pool clears MinimumGames.
        for (int i = 0; i < 8; i++)
        {
            await SeedRatedGameAsync(
                subject.UserId, evenOpponent.UserId, GameResult.WhiteWins, TerminationReason.Checkmate, "steal1", plies: 40,
                endedAt.AddHours(12 + i), subjectBefore: 1500, subjectAfter: 1520, opponentBefore: 1500, opponentAfter: 1480);
        }

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{subject.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reports = await response.Content.ReadFromJsonAsync<DetectionReportDto[]>(JsonOptions);
        var report = Assert.Single(reports!);
        var lossesToWeaker = Assert.Single(report.Signals, s => s.Name == "losses-to-weaker");

        // If the pairing bug defaulted the opponent's rating to the subject's own (1500), the gap on
        // every one of these 12 losses would compute as 0, well under the 150 threshold, and this
        // would read "Lost 0 of 12" instead.
        Assert.Contains("Lost 12 of 12", lossesToWeaker.Evidence);
    }

    [SkippableFact]
    public async Task Per_user_detection_is_logged_against_the_moderator()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-log-admin@example.com", "AdminUser");
        var subject = await RegisterAsync("sandbagging-log-subject@example.com", "Subject");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/detect/sandbagging/{subject.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 9d.3: a per-player detector run is a read about one named player and must be attributable.
        // StartsWith("Moderator ") excludes unrelated log lines that happen to carry the admin's own
        // id, such as their registration confirmation email (which embeds it in the link).
        Assert.Contains(_logs.Messages, m =>
            m.StartsWith("Moderator ") && m.Contains(admin.UserId.ToString()) && m.Contains(subject.UserId.ToString()));
    }

    [SkippableFact]
    public async Task Sweep_is_logged_against_the_moderator_once_not_per_candidate()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-sweep-log-admin@example.com", "AdminUser");

        AuthorizeAs(admin);
        var response = await _client!.GetAsync("/api/moderation/detect/sandbagging?take=5&minScore=0&sinceDays=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 9d.3: one line per sweep, naming the moderator - not one per candidate examined.
        Assert.Single(_logs.Messages, m => m.StartsWith("Moderator ") && m.Contains(admin.UserId.ToString()));
    }

    [SkippableFact]
    public async Task Sweep_returns_200_and_respects_take()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("sandbagging-sweep-admin@example.com", "AdminUser");
        var playerA = await RegisterAsync("sandbagging-sweep-a@example.com", "Player A");
        var playerB = await RegisterAsync("sandbagging-sweep-b@example.com", "Player B");

        // Three low-volume fillers so no single opponent individually accumulates MinimumGames worth
        // of decisive rated games and becomes an unwanted third candidate.
        var fillers = new[]
        {
            await RegisterAsync("sandbagging-sweep-filler-0@example.com", "Filler 0"),
            await RegisterAsync("sandbagging-sweep-filler-1@example.com", "Filler 1"),
            await RegisterAsync("sandbagging-sweep-filler-2@example.com", "Filler 2"),
        };

        var endedAt = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var candidate in new[] { playerA, playerB })
        {
            for (int i = 0; i < 20; i++)
            {
                var result = i % 2 == 0 ? GameResult.WhiteWins : GameResult.BlackWins;
                await SeedRatedGameAsync(
                    candidate.UserId, fillers[i % fillers.Length].UserId, result, TerminationReason.Checkmate,
                    "steal1", plies: 40, endedAt.AddHours(i),
                    subjectBefore: 1500, subjectAfter: 1500, opponentBefore: 1500, opponentAfter: 1500);
            }
        }

        AuthorizeAs(admin);

        var capped = await _client!.GetFromJsonAsync<DetectionReportDto[]>(
            "/api/moderation/detect/sandbagging?take=1&minScore=0&sinceDays=30", JsonOptions);
        Assert.NotNull(capped);
        Assert.Single(capped!);

        var uncapped = await _client!.GetFromJsonAsync<DetectionReportDto[]>(
            "/api/moderation/detect/sandbagging?take=25&minScore=0&sinceDays=30", JsonOptions);
        Assert.NotNull(uncapped);
        Assert.Equal(2, uncapped!.Length);
        Assert.All(uncapped, r => Assert.Equal("sandbagging", r.Detector));
    }

    private async Task SeedRatedGameAsync(
        Guid whiteId, Guid blackId, GameResult result, TerminationReason termination,
        string pool, int plies, DateTimeOffset endedAt,
        double subjectBefore, double subjectAfter, double opponentBefore, double opponentAfter)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var gameId = Guid.NewGuid();
        db.Games.Add(new Game
        {
            Id = gameId,
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = result,
            Termination = termination,
            RatingPool = pool,
            IsRated = true,
            StartFen = string.Empty,
            ConfigSnapshot = new KiriChessConfig(),
            Moves = new List<MoveRecord>(),
            PlyCount = plies,
            CreatedAt = endedAt.AddMinutes(-5),
            EndedAt = endedAt,
        });

        // whiteId is "the subject" in the caller's frame here (the caller decides which side wins via
        // result). Both RatingHistory rows are always written, one per player, matching what
        // RatingService.ApplyAsync does for a real rated game.
        db.RatingHistories.Add(new RatingHistory
        {
            GameId = gameId, UserId = whiteId, Pool = pool,
            ValueBefore = subjectBefore, ValueAfter = subjectAfter,
            RdBefore = 100, RdAfter = 100, VolatilityBefore = 0.06, VolatilityAfter = 0.06,
            CreatedAt = endedAt,
        });
        db.RatingHistories.Add(new RatingHistory
        {
            GameId = gameId, UserId = blackId, Pool = pool,
            ValueBefore = opponentBefore, ValueAfter = opponentAfter,
            RdBefore = 100, RdAfter = 100, VolatilityBefore = 0.06, VolatilityAfter = 0.06,
            CreatedAt = endedAt,
        });

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
    /// token actually carries the role claim - copied from ModerationEndpointTests for the same reason
    /// stated there: proving the round trip, not hand-minting a token that merely looks right.
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
