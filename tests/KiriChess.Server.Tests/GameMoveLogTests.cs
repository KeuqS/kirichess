using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class GameMoveLogTests
{
    private static MoveRecord Ply(int ply, string uci) =>
        new(ply, uci, Side.White, 900, 900, 0, 0, "Ongoing");

    [Fact]
    public void Merge_keeps_the_stored_prefix_and_appends_later_plies()
    {
        var merged = GameMoveLog.Merge(
            [Ply(1, "e2e4")],
            [Ply(2, "e7e5")]);

        Assert.Equal(["e2e4", "e7e5"], merged.Select(m => m.Uci).ToArray());
    }

    [Fact]
    public void Merge_prefers_an_appended_ply_when_both_name_it()
    {
        var merged = GameMoveLog.Merge(
            [Ply(1, "e2e4")],
            [Ply(1, "d2d4")]);

        Assert.Equal("d2d4", Assert.Single(merged).Uci);
    }

    [Fact]
    public void Merge_of_an_empty_stored_list_is_the_append_log()
    {
        var merged = GameMoveLog.Merge([], [Ply(1, "e2e4"), Ply(2, "e7e5")]);

        Assert.Equal([1, 2], merged.Select(m => m.Ply).ToArray());
    }
}
