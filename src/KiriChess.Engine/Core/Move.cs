using System;

namespace KiriChess.Engine.Core;

/// <summary>
/// A move expressed in engine-neutral terms: origin/destination squares in algebraic
/// notation ("e2", "e4") plus an optional promotion target. Deliberately independent of the
/// underlying chess library so the public API never leaks it.
/// </summary>
public sealed record Move
{
    public string From { get; }
    public string To { get; }

    /// <summary>Piece to promote to (Queen/Rook/Bishop/Knight). Null for non-promotion moves.</summary>
    public PieceType? Promotion { get; }

    public Move(string from, string to, PieceType? promotion = null)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("From square is required", nameof(from));
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("To square is required", nameof(to));
        From = from.Trim().ToLowerInvariant();
        To = to.Trim().ToLowerInvariant();
        Promotion = promotion;
    }

    /// <summary>Parses UCI-style coordinate notation: "e2e4", "e7e8q".</summary>
    public static Move Parse(string uci)
    {
        if (string.IsNullOrWhiteSpace(uci)) throw new ArgumentException("Move text is required", nameof(uci));
        uci = uci.Trim().ToLowerInvariant();
        if (uci.Length is < 4 or > 5)
            throw new FormatException($"'{uci}' is not valid UCI coordinate notation (expected e.g. 'e2e4' or 'e7e8q').");

        var from = uci.Substring(0, 2);
        var to = uci.Substring(2, 2);
        PieceType? promo = uci.Length == 5 ? PromotionFromChar(uci[4]) : null;
        return new Move(from, to, promo);
    }

    private static PieceType PromotionFromChar(char c) => c switch
    {
        'q' => PieceType.Queen,
        'r' => PieceType.Rook,
        'b' => PieceType.Bishop,
        'n' => PieceType.Knight,
        _ => throw new FormatException($"'{c}' is not a valid promotion piece.")
    };

    public override string ToString() =>
        Promotion is null ? $"{From}{To}" : $"{From}{To}{PromotionToChar(Promotion.Value)}";

    private static char PromotionToChar(PieceType t) => t switch
    {
        PieceType.Queen => 'q',
        PieceType.Rook => 'r',
        PieceType.Bishop => 'b',
        PieceType.Knight => 'n',
        _ => throw new ArgumentException($"{t} is not a valid promotion piece.")
    };
}
