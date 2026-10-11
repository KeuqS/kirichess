using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 6e.2 report intake, end to end: happy path, every validation branch, duplicate suppression,
/// and that <c>/mine</c> is scoped to the caller. Requires Docker; skips cleanly (via
/// <see cref="SkippableFactAttribute"/>) when no Docker daemon is reachable.
/// </summary>
public sealed class ReportEndpointTests : IAsyncLifetime
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

        var connectionString = _pg.GetConnectionString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
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
    public async Task Happy_path_creates_a_report_row()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("report-happy-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("report-happy-reported@example.com", "Reported");
        AuthorizeAs(reporter);

        var response = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "EngineAssistance",
            Details = "Suspiciously uniform move times."
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReportDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(reported.UserId, dto!.ReportedUserId);
        Assert.Equal("EngineAssistance", dto.Reason);
        Assert.Equal("Open", dto.Status);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Reports.SingleAsync(r => r.Id == dto.Id);
        Assert.Equal(reporter.UserId, row.ReporterId);
        Assert.Equal(reported.UserId, row.ReportedUserId);
        Assert.Equal(ReportReason.EngineAssistance, row.Reason);
    }

    [SkippableFact]
    public async Task Self_report_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("report-self@example.com", "Self Reporter");
        AuthorizeAs(reporter);

        var response = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reporter.UserId,
            GameId = (Guid?)null,
            Reason = "Other",
            Details = (string?)null
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unknown_reported_user_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("report-unknown-user@example.com", "Reporter");
        AuthorizeAs(reporter);

        var response = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = Guid.NewGuid(),
            GameId = (Guid?)null,
            Reason = "Other",
            Details = (string?)null
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unknown_reason_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("report-bad-reason-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("report-bad-reason-reported@example.com", "Reported");
        AuthorizeAs(reporter);

        var response = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "NotARealReason",
            Details = (string?)null
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task GameId_the_reporter_did_not_play_in_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Two players who play a game together, the game the reporter is NOT part of.
        var playerA = await RegisterAsync("report-game-a@example.com", "Player A");
        var playerB = await RegisterAsync("report-game-b@example.com", "Player B");
        var gameId = await CreateAndFinishGameAsync(playerA, playerB);

        var reporter = await RegisterAsync("report-game-outsider@example.com", "Outsider");
        AuthorizeAs(reporter);

        var response = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = playerA.UserId,
            GameId = gameId,
            Reason = "Stalling",
            Details = (string?)null
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Duplicate_open_report_returns_the_existing_row_not_a_second_one()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("report-dup-reporter@example.com", "Reporter");
        var reported = await RegisterAsync("report-dup-reported@example.com", "Reported");
        AuthorizeAs(reporter);

        var body = new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = "First submission."
        };

        var first = await _client!.PostAsJsonAsync("/api/reports", body, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<ReportDto>(JsonOptions);

        var second = await _client.PostAsJsonAsync("/api/reports", body, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondDto = await second.Content.ReadFromJsonAsync<ReportDto>(JsonOptions);

        Assert.Equal(firstDto!.Id, secondDto!.Id);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var count = await db.Reports.CountAsync(r =>
            r.ReporterId == reporter.UserId && r.ReportedUserId == reported.UserId);
        Assert.Equal(1, count);
    }

    [SkippableFact]
    public async Task Mine_returns_only_the_callers_reports()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporterOne = await RegisterAsync("report-mine-one@example.com", "Reporter One");
        var reporterTwo = await RegisterAsync("report-mine-two@example.com", "Reporter Two");
        var target = await RegisterAsync("report-mine-target@example.com", "Target");

        AuthorizeAs(reporterOne);
        var createOne = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = target.UserId,
            GameId = (Guid?)null,
            Reason = "Sandbagging",
            Details = (string?)null
        }, JsonOptions);
        createOne.EnsureSuccessStatusCode();

        AuthorizeAs(reporterTwo);
        var createTwo = await _client.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = target.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = (string?)null
        }, JsonOptions);
        createTwo.EnsureSuccessStatusCode();

        AuthorizeAs(reporterOne);
        var mine = await _client.GetAsync("/api/reports/mine");
        mine.EnsureSuccessStatusCode();
        var rows = await mine.Content.ReadFromJsonAsync<ReportDto[]>(JsonOptions);

        Assert.NotNull(rows);
        Assert.All(rows!, r => Assert.Equal(target.UserId, r.ReportedUserId));
        Assert.Single(rows!, r => r.Reason == "Sandbagging");
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

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

    /// <summary>
    /// Creates a two-player unrated game and resigns it, authorized as <paramref name="white"/>.
    ///
    /// Phase 9d.8 closed POST /api/games' OpponentId hole (README scope decision 5), so a two-distinct-
    /// player game is now created the same way a real player would: a challenge, created by white and
    /// accepted by black, colour pinned to White so the rest of this helper's "white resigns" contract
    /// is unchanged.
    /// </summary>
    private async Task<Guid> CreateAndFinishGameAsync(AuthResult white, AuthResult black)
    {
        AuthorizeAs(white);
        var challengeResponse = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "steal1",
            Colour = "White"
        }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        AuthorizeAs(black);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(accepted);
        var gameId = accepted!.GameId;

        AuthorizeAs(white);
        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();
        return gameId;
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record GameStateDto(
        Guid GameId,
        string Fen,
        string SideToMove,
        int WhiteTime,
        int BlackTime,
        string Status,
        string? Winner,
        string Termination,
        Guid WhitePlayerId,
        Guid BlackPlayerId,
        int MoveCount);

    private sealed record ReportDto(
        Guid Id, Guid ReportedUserId, Guid? GameId, string Reason, string? Details, string Status, DateTimeOffset CreatedAt);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record ChallengeAcceptResultDto(Guid GameId);
}
