using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// A live snapshot inserts <see cref="GameMove"/> rows and does not mark the <see cref="Game"/>
/// modified. If it did, SaveChanges would rewrite <c>Moves</c> and could blank columns the snapshot
/// does not own. Final results still belong to <c>GameRegistry.PersistIfOverAsync</c>. These tests
/// need no Docker: the change tracker settles before SaveChanges would open a connection.
/// </summary>
public sealed class SnapshotWriteShapeTests
{
    private static DbContextOptions<KiriChessDbContext> Options() =>
        new DbContextOptionsBuilder<KiriChessDbContext>()
            .UseNpgsql("Host=localhost;Database=kirichess_snapshot_shape;Username=none;Password=none")
            .Options;

    private static MoveRecord Ply(int ply) =>
        new(ply, "e2e4", Side.White, 900, 900, 0, 0, "Ongoing");

    [Fact]
    public void Appending_a_ply_does_not_modify_the_game_row()
    {
        using var db = new KiriChessDbContext(Options());
        var gameId = Guid.NewGuid();
        var game = new Game { Id = gameId, StartFen = "start", PlyCount = 0 };
        db.Games.Attach(game);
        db.GameMoves.Add(new GameMove { GameId = gameId, Ply = 1, Move = Ply(1) });

        Assert.Equal(EntityState.Unchanged, db.Entry(game).State);
        Assert.Equal(EntityState.Added, db.Entry(db.GameMoves.Local.Single()).State);
    }
}
