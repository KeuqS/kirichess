using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
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
/// End-to-end coverage of the Phase 8.5 Task C routes:
/// <c>GET /api/moderation/detect/engine/{userId}</c> and <c>.../game/{gameId}</c>.
/// There is deliberately no sweep sibling (B20a / S-2-3). Thin by design - the
/// scoring maths is covered without Docker in EngineAssistanceAnalyzerTests; this class only proves the
/// wiring (auth, 404s, and the White/Black side mapping) since that is the part a pure unit test cannot
/// see. Requires Docker; skips cleanly (via <see cref="SkippableFactAttribute"/>) when no Docker daemon
/// is reachable.
/// </summary>
public sealed class EngineDetectionEndpointTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "engine-detection-endpoint-test-signing-key-32bytes",
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
    public async Task Non_admin_gets_403()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("engine-non-admin@example.com", "Regular");
        AuthorizeAs(user);

        var response = await _client!.GetAsync($"/api/moderation/detect/engine/{user.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unauthenticated_gets_401()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync($"/api/moderation/detect/engine/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_and_unknown_user_is_404()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("engine-unknown-user-admin@example.com", "AdminUser");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync($"/api/moderation/detect/engine/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_and_unknown_game_is_404()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("engine-unknown-game-admin@example.com", "AdminUser");
        AuthorizeAs(admin);

        var response = await _client!.GetAsync($"/api/moderation/detect/engine/game/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_and_a_seeded_game_returns_200_with_the_engine_assistance_detector_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("engine-happy-path-admin@example.com", "AdminUser");
        var white = await RegisterAsync("engine-happy-path-white@example.com", "White Player");
        var black = await RegisterAsync("engine-happy-path-black@example.com", "Black Player");
        await SeedAsymmetricGameAsync(white.UserId, black.UserId);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/detect/engine/{white.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<DetectionReportResultDto>(JsonOptions);
        Assert.NotNull(report);
        Assert.Equal("engine-assistance", report!.Detector);
        Assert.Null(report.Pool);
    }

    [SkippableFact]
    public async Task Subject_plies_are_read_from_the_correct_side()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // White's post-opening plies are all in severe time trouble (dropped); Black's are all
        // comfortably scorable. If the endpoint's White/Black mapping were backwards, White's report
        // would show Black's non-zero sample size (or vice versa) - a plausible, entirely wrong report,
        // exactly the failure mode this test exists to catch.
        var admin = await RegisterAndPromoteAsync("engine-side-mapping-admin@example.com", "AdminUser");
        var white = await RegisterAsync("engine-side-mapping-white@example.com", "White Player");
        var black = await RegisterAsync("engine-side-mapping-black@example.com", "Black Player");
        await SeedAsymmetricGameAsync(white.UserId, black.UserId);

        AuthorizeAs(admin);

        var whiteResponse = await _client!.GetAsync($"/api/moderation/detect/engine/{white.UserId}");
        whiteResponse.EnsureSuccessStatusCode();
        var whiteReport = await whiteResponse.Content.ReadFromJsonAsync<DetectionReportResultDto>(JsonOptions);

        var blackResponse = await _client.GetAsync($"/api/moderation/detect/engine/{black.UserId}");
        blackResponse.EnsureSuccessStatusCode();
        var blackReport = await blackResponse.Content.ReadFromJsonAsync<DetectionReportResultDto>(JsonOptions);

        Assert.NotNull(whiteReport);
        Assert.NotNull(blackReport);
        Assert.Equal(0, whiteReport!.SampleSize);
        Assert.Equal(16, blackReport!.SampleSize);
    }

    [SkippableFact]
    public async Task Per_game_route_returns_two_reports_for_a_two_player_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("engine-per-game-admin@example.com", "AdminUser");
        var white = await RegisterAsync("engine-per-game-white@example.com", "White Player");
        var black = await RegisterAsync("engine-per-game-black@example.com", "Black Player");
        var gameId = await SeedAsymmetricGameAsync(white.UserId, black.UserId);

        AuthorizeAs(admin);
        var response = await _client!.GetAsync($"/api/moderation/detect/engine/game/{gameId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reports = await response.Content.ReadFromJsonAsync<DetectionReportResultDto[]>(JsonOptions);
        Assert.NotNull(reports);
        Assert.Equal(2, reports!.Length);
        // A single game essentially never clears MinimumGames on its own - Insufficient here is the
        // correct behaviour, not a bug to fix by lowering the thresholds for this route.
        Assert.All(reports, r => Assert.Equal("Insufficient", r.Verdict));
    }

    [SkippableFact]
    public async Task Bot_pool_games_are_excluded_from_the_subject_sample()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("engine-bot-pool-admin@example.com", "AdminUser");
        var white = await RegisterAsync("engine-bot-pool-white@example.com", "White Player");
        var black = await RegisterAsync("engine-bot-pool-black@example.com", "Black Player");
        await SeedAsymmetricGameAsync(white.UserId, black.UserId, ratingPool: "bot:steal1");

        AuthorizeAs(admin);
        var blackResponse = await _client!.GetAsync($"/api/moderation/detect/engine/{black.UserId}");
        blackResponse.EnsureSuccessStatusCode();
        var blackReport = await blackResponse.Content.ReadFromJsonAsync<DetectionReportResultDto>(JsonOptions);

        Assert.NotNull(blackReport);
        Assert.Equal(0, blackReport!.SampleSize);
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

    /// <summary>Same pattern as ModerationEndpointTests: promote through UserManager, then log back in for a token carrying the role claim.</summary>
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

    /// <summary>
    /// Inserts a finished, rated 40-ply game straight through the DbContext - the detector reads
    /// telemetry off stored MoveRecord rows, so nothing here needs a running engine or a real move
    /// sequence. Ply 1..8 (the opening) alternates White/Black; Ply 9..40 alternates too, giving each
    /// side 16 post-opening plies. White's 16 are all in severe time trouble (WhiteTimeAfter == 5,
    /// under the 10s floor) so none are scorable; Black's 16 all have WhiteTimeAfter/BlackTimeAfter
    /// well clear of the floor with real telemetry, so all 16 are scorable.
    /// </summary>
    private async Task<Guid> SeedAsymmetricGameAsync(Guid whiteId, Guid blackId, string ratingPool = "steal1")
    {
        var received = DateTimeOffset.UtcNow.AddDays(-1);
        var moves = new List<MoveRecord>();

        for (int ply = 1; ply <= 40; ply++)
        {
            bool isWhitePly = ply % 2 == 1;
            var mover = isWhitePly ? Side.White : Side.Black;

            int whiteTimeAfter = isWhitePly ? 5 : 200;   // White always in time trouble; Black never
            int blackTimeAfter = isWhitePly ? 200 : 200;

            moves.Add(new MoveRecord(
                ply, "e2e4", mover, whiteTimeAfter, blackTimeAfter, -5, -5, "Ongoing",
                ThinkMs: 3000, ReceivedAtUtc: received, LagMs: 0));
        }

        var game = new Game
        {
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = GameResult.WhiteWins,
            Termination = TerminationReason.Checkmate,
            RatingPool = ratingPool,
            IsRated = true,
            StartFen = "startpos",
            ConfigSnapshot = KiriChessConfig.Presets["steal1"].Clone(),
            Moves = moves,
            PlyCount = moves.Count,
            EndedAt = DateTimeOffset.UtcNow,
        };

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game.Id;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);

    private sealed record DetectionReportResultDto(
        Guid UserId, string DisplayName, string Detector, string? Pool,
        string Verdict, double Score, int SampleSize,
        DetectionSignalResultDto[] Signals, string[] Caveats, DateTimeOffset ComputedAt);

    private sealed record DetectionSignalResultDto(
        string Name, string Label, double Value, double Contribution, string Evidence);
}
