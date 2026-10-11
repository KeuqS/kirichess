using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KiriChess.Engine.Tooling;

public sealed record PieceInts5(int Pawn, int Knight, int Bishop, int Rook, int Queen);

public sealed record PieceInts6(int Pawn, int Knight, int Bishop, int Rook, int Queen, int King);

/// <summary>Same field set and camelCase names as the server's EconomyDto, so a vector's <c>economy</c>
/// object is exactly what a real /api/games response's <c>economy</c> field looks like.</summary>
public sealed record EconomyJson(
    int StartTimeSeconds,
    int EconomyScale,
    PieceInts5 PieceValues,
    int CaptureMult,
    bool CaptureCaptorGainEnabled,
    bool CaptureVictimLossEnabled,
    string MoveCostMode,
    PieceInts6 MoveCostPerPiece,
    int CastleCostFlat,
    int MoveCostFlat,
    int MoveCostPerSquare,
    int PromotionCostMult,
    int CheckCheckerGain,
    int CheckVictimLoss,
    int DoubleCheckChipMultiplier,
    int BaseIncrement,
    bool RepeatedPositionNullifiesTime,
    bool ThreefoldRepetitionIsDraw,
    bool FlagFallLoses,
    bool FlagVsInsufficientMaterialIsDraw);

/// <summary>Same field set as ui/lib/costPreview.ts's <c>CostPreviewResult</c>.</summary>
public sealed record ExpectedJson(
    int MoverNet,
    bool Capture,
    string? VictimType,
    int OpponentLoss,
    bool HasCheckChip);

public sealed record VectorJson(
    string Label,
    string Fen,
    string From,
    string To,
    string? Promotion,
    EconomyJson Economy,
    ExpectedJson Expected);

public sealed record CostVectorFile(
    [property: JsonPropertyName("$comment")] IReadOnlyList<string> Comment,
    IReadOnlyList<VectorJson> Vectors);
