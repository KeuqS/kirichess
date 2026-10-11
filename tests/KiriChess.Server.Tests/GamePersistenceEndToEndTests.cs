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
/// End-to-end proof of the Phase 2 exit criterion: create a game over HTTP, play/resign it, and
/// confirm it lands in Postgres exactly once. Requires Docker; skips cleanly (via
/// <see cref="SkippableFactAttribute"/>) when no Docker daemon is reachable.
/// </summary>
public sealed class GamePersistenceEndToEndTests : IAsyncLifetime
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
    public async Task Finished_game_persists_via_resignation()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "e2e-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "E2E Player",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var createResponse = await _client.PostAsJsonAsync("/api/games", new
        {
            OpponentId = (Guid?)null,
            Preset = "burn2",
            Rated = true
        }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(created);
        var gameId = created!.GameId;

        var legalMovesResponse = await _client.GetAsync($"/api/games/{gameId}/legal-moves");
        legalMovesResponse.EnsureSuccessStatusCode();
        var legalMoves = await legalMovesResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.NotNull(legalMoves);
        Assert.NotEmpty(legalMoves!);

        var moveResponse = await _client.PostAsJsonAsync($"/api/games/{gameId}/moves", new { Uci = legalMoves![0] }, JsonOptions);
        moveResponse.EnsureSuccessStatusCode();

        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();
        var resigned = await resignResponse.Content.ReadFromJsonAsync<GameStateDto>(JsonOptions);
        Assert.NotNull(resigned);
        Assert.Equal("Resignation", resigned!.Termination);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var persisted = await db.Games.SingleAsync(g => g.Id == gameId);

        Assert.Equal(TerminationReason.Resignation, persisted.Termination);
        Assert.NotEmpty(persisted.Moves);
        Assert.False(persisted.ConfigSnapshot.CaptureCaptorGainEnabled); // burn2 preset
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
}
