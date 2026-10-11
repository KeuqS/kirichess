namespace KiriChess.Engine.Core;

/// <summary>Which player a value refers to. The engine is UI/network-agnostic; this is its only notion of "colour".</summary>
public enum Side
{
    White,
    Black
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side == Side.White ? Side.Black : Side.White;
}
