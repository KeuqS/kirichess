using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
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
/// Proves the Phase 6e.3 abandonment cooldown query end to end against a real Postgres, the
/// predicate translates to SQL, so an in-memory list would not exercise the part that matters.
/// Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class AbandonmentPolicyTests : IAsyncLifetime
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
                    ["Jwt:ExpiryMinutes"] = "60",
                    // Small, deterministic values so the tests don't depend on the production defaults.
                    ["Matchmaking:AbandonWindowHours"] = "24",
                    ["Matchmaking:AbandonThreshold"] = "3",
                    ["Matchmaking:AbandonCooldownMinutes"] = "15"
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
    public async Task Under_the_threshold_is_allowed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var loser = await RegisterAsync("abandon-under-1@example.com", "Under Threshold");
        var winner = await RegisterAsync("abandon-under-2@example.com", "Under Threshold Opponent");

        // Threshold is 3; seed only 2 abandoned losses.
        await SeedAbandonedLossAsync(loser.UserId, winner.UserId, DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedAbandonedLossAsync(loser.UserId, winner.UserId, DateTimeOffset.UtcNow.AddMinutes(-3));

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(loser.UserId);

        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task At_threshold_within_cooldown_is_blocked_with_positive_retry_after()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var loser = await RegisterAsync("abandon-block-1@example.com", "At Threshold");
        var winner = await RegisterAsync("abandon-block-2@example.com", "At Threshold Opponent");

        for (var i = 0; i < 3; i++)
            await SeedAbandonedLossAsync(loser.UserId, winner.UserId, DateTimeOffset.UtcNow.AddMinutes(-1 - i));

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(loser.UserId);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.RetryAfter > TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task At_threshold_but_most_recent_offence_older_than_cooldown_is_allowed_again()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var loser = await RegisterAsync("abandon-decay-1@example.com", "Decayed Cooldown");
        // "Decayed Cooldown Foe" (not "... Opponent"): the longer form is 25 characters, one over the
        // 24-character ContentPolicy MaxLength (Phase 8a.4) - this is fixture data, not a policy bug.
        var winner = await RegisterAsync("abandon-decay-2@example.com", "Decayed Cooldown Foe");

        // Cooldown is 15 minutes; the most recent offence is an hour old (still inside the 24h
        // window, so it counts toward the threshold, but the cooldown itself has decayed).
        for (var i = 0; i < 3; i++)
            await SeedAbandonedLossAsync(loser.UserId, winner.UserId, DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-i));

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(loser.UserId);

        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task ServerRestart_aborts_do_not_count_toward_the_cooldown()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("abandon-restart-1@example.com", "Restarted Often");
        var opponent = await RegisterAsync("abandon-restart-2@example.com", "Restarted Often Opponent");

        // Several ServerRestart (and plain no-winner Aborted) games, none of these should ever
        // count, because an abort assigns no fault. This is the test that protects 6c.3's promise
        // that a server restart never costs a player anything.
        for (var i = 0; i < 5; i++)
        {
            await SeedGameAsync(
                user.UserId, opponent.UserId, GameResult.Aborted, TerminationReason.ServerRestart,
                DateTimeOffset.UtcNow.AddMinutes(-1 - i));
        }

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(user.UserId);

        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task The_winner_of_an_abandoned_game_is_not_penalised()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var loser = await RegisterAsync("abandon-winner-1@example.com", "Serial Abandoner");
        var winner = await RegisterAsync("abandon-winner-2@example.com", "Innocent Winner");

        // The abandoner (loser) racks up enough offences to be blocked...
        for (var i = 0; i < 3; i++)
            await SeedAbandonedLossAsync(loser.UserId, winner.UserId, DateTimeOffset.UtcNow.AddMinutes(-1 - i));

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var loserVerdict = await policy.EvaluateAsync(loser.UserId);
        var winnerVerdict = await policy.EvaluateAsync(winner.UserId);

        Assert.False(loserVerdict.Allowed);
        Assert.True(winnerVerdict.Allowed); // ...but the opponent who never abandoned anything is untouched.
    }

    [SkippableFact]
    public async Task Three_attributed_NoShows_in_the_window_are_blocked()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("abandon-noshow-1@example.com", "Serial NoShow");
        var opponent = await RegisterAsync("abandon-noshow-2@example.com", "NoShow Opponent");

        for (var i = 0; i < 3; i++)
        {
            await SeedGameAsync(
                user.UserId, opponent.UserId, GameResult.Aborted, TerminationReason.NoShow,
                DateTimeOffset.UtcNow.AddMinutes(-1 - i), noShowAtFaultPlayerId: user.UserId);
        }

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(user.UserId);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.RetryAfter > TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task NoShows_attributed_to_the_opponent_do_not_charge_the_player_who_showed_up()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("abandon-noshow-opp-1@example.com", "Showed Up");
        var opponent = await RegisterAsync("abandon-noshow-opp-2@example.com", "Missing Seat");

        for (var i = 0; i < 3; i++)
        {
            await SeedGameAsync(
                user.UserId, opponent.UserId, GameResult.Aborted, TerminationReason.NoShow,
                DateTimeOffset.UtcNow.AddMinutes(-1 - i), noShowAtFaultPlayerId: opponent.UserId);
        }

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(user.UserId);

        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task Both_missing_NoShows_do_not_count()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("abandon-noshow-both-1@example.com", "Both Missing A");
        var opponent = await RegisterAsync("abandon-noshow-both-2@example.com", "Both Missing B");

        for (var i = 0; i < 3; i++)
        {
            await SeedGameAsync(
                user.UserId, opponent.UserId, GameResult.Aborted, TerminationReason.NoShow,
                DateTimeOffset.UtcNow.AddMinutes(-1 - i), noShowAtFaultPlayerId: null);
        }

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(user.UserId);

        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task Mixed_Abandoned_and_attributed_NoShow_share_the_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("abandon-noshow-mix-1@example.com", "Mixed Offences");
        var opponent = await RegisterAsync("abandon-noshow-mix-2@example.com", "Mixed Opponent");

        await SeedAbandonedLossAsync(user.UserId, opponent.UserId, DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedAbandonedLossAsync(user.UserId, opponent.UserId, DateTimeOffset.UtcNow.AddMinutes(-3));
        await SeedGameAsync(
            user.UserId, opponent.UserId, GameResult.Aborted, TerminationReason.NoShow,
            DateTimeOffset.UtcNow.AddMinutes(-1), noShowAtFaultPlayerId: user.UserId);

        var policy = _factory!.Services.GetRequiredService<AbandonmentPolicy>();
        var verdict = await policy.EvaluateAsync(user.UserId);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.RetryAfter > TimeSpan.Zero);
    }

    /// <summary>Seeds an Abandoned game where <paramref name="loserId"/> is the losing (abandoning) side.</summary>
    private async Task SeedAbandonedLossAsync(Guid loserId, Guid winnerId, DateTimeOffset endedAt)
    {
        // Randomise which colour the loser plays so the predicate's OR-branch is genuinely exercised,
        // not just the White-loses branch.
        bool loserIsWhite = Random.Shared.Next(2) == 0;
        var result = loserIsWhite ? GameResult.BlackWins : GameResult.WhiteWins;
        var white = loserIsWhite ? loserId : winnerId;
        var black = loserIsWhite ? winnerId : loserId;
        await SeedGameAsync(white, black, result, TerminationReason.Abandoned, endedAt);
    }

    private async Task SeedGameAsync(
        Guid whiteId, Guid blackId, GameResult result, TerminationReason termination, DateTimeOffset endedAt,
        Guid? noShowAtFaultPlayerId = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = whiteId,
            BlackPlayerId = blackId,
            Result = result,
            Termination = termination,
            RatingPool = "steal1",
            IsRated = true,
            StartFen = string.Empty,
            ConfigSnapshot = new KiriChessConfig(),
            Moves = new List<MoveRecord>(),
            CreatedAt = endedAt.AddMinutes(-1),
            EndedAt = endedAt,
            NoShowAtFaultPlayerId = noShowAtFaultPlayerId,
        });
        await db.SaveChangesAsync();
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

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);
}
