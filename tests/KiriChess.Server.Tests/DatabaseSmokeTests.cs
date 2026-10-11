using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end jsonb round-trip check against a real Postgres instance. Requires Docker; skips
/// cleanly (via <see cref="SkippableFactAttribute"/>) when no Docker daemon is reachable.
/// </summary>
public sealed class DatabaseSmokeTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Game_with_jsonb_round_trips()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = new DbContextOptionsBuilder<KiriChessDbContext>()
            .UseNpgsql(_pg!.GetConnectionString())
            .Options;

        await using var db = new KiriChessDbContext(options);
        await db.Database.MigrateAsync();

        var white = new ApplicationUser { UserName = "w@x.io", Email = "w@x.io", DisplayName = "W" };
        var black = new ApplicationUser { UserName = "b@x.io", Email = "b@x.io", DisplayName = "B" };
        db.Users.AddRange(white, black);

        var game = new Game
        {
            WhitePlayerId = white.Id,
            BlackPlayerId = black.Id,
            Result = GameResult.WhiteWins,
            Termination = TerminationReason.Flagfall,
            RatingPool = "steal1",
            IsRated = true,
            StartFen = KiriChessEngine.StartFen,
            ConfigSnapshot = KiriChessConfig.CreateBurn2(),
            Moves = { new MoveRecord(1, "e2e4", Side.White, 179, 180, 0, 0, "Ongoing") },
            EndedAt = DateTimeOffset.UtcNow
        };
        db.Games.Add(game);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var loaded = await db.Games.SingleAsync(g => g.Id == game.Id);
        Assert.False(loaded.ConfigSnapshot.CaptureCaptorGainEnabled); // burn2 preset
        Assert.Single(loaded.Moves);
        Assert.Equal("e2e4", loaded.Moves[0].Uci);
    }
}
