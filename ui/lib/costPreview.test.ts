import { describe, expect, it } from "vitest";
import {
  computeCostPreview,
  economyFromPreset,
  movementToll,
} from "./costPreview";
import { PRESETS } from "./presets";
import { DEFAULT_RULES } from "./rules.generated";
import type { EconomyConfig } from "./types";

/**
 * Oracle vectors for the capture-economy cases below are the actual configs and FENs used by
 * tests/KiriChess.Engine.Tests/Rule1CaptureEconomyTests.cs (the engine's own tests for the code
 * this module mirrors: KiriChessEngine.ApplyEconomy) so a divergence here means the client-side
 * preview would lie to a player. `captureOnlyEconomy` reproduces
 * tests/KiriChess.Engine.Tests/TestSupport.cs's `CaptureOnly` fixture: default piece values from
 * the same generated rules the engine embeds, capture mult forced to 5, everything else that
 * would add noise (tolls, checks, promotion, castling) turned off.
 */
function captureOnlyEconomy(
  gainEnabled: boolean,
  lossEnabled: boolean,
): EconomyConfig {
  return {
    startTimeSeconds: 180,
    economyScale: DEFAULT_RULES.economyScale,
    pieceValues: { ...DEFAULT_RULES.pieceValues },
    captureMult: 5,
    captureCaptorGainEnabled: gainEnabled,
    captureVictimLossEnabled: lossEnabled,
    moveCostMode: "None",
    moveCostPerPiece: { ...DEFAULT_RULES.moveCostPerPiece },
    castleCostFlat: 0,
    moveCostFlat: 0,
    moveCostPerSquare: 0,
    promotionCostMult: 0,
    checkCheckerGain: 0,
    checkVictimLoss: 0,
    doubleCheckChipMultiplier: 1,
    baseIncrement: 0,
    repeatedPositionNullifiesTime: false,
    threefoldRepetitionIsDraw: false,
    flagFallLoses: true,
    flagVsInsufficientMaterialIsDraw: true,
  };
}

describe("computeCostPreview: capture economy (oracle: Rule1CaptureEconomyTests.cs)", () => {
  // Rule1CaptureEconomyTests.PawnTradeFen: white e4 pawn, black c6/d5 pawns.
  const pawnTradeFen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1";

  it("Steal (gain+loss enabled): mover gains, opponent loses the same amount: Steal_EqualTrade_LeavesClockGapUnchanged", () => {
    const result = computeCostPreview(
      pawnTradeFen,
      "e4",
      "d5",
      captureOnlyEconomy(true, true),
    );
    expect(result.capture).toBe(true);
    expect(result.victimType).toBe("p");
    expect(result.moverNet).toBe(5); // pawn value 1 * captureMult 5
    expect(result.opponentLoss).toBe(5);
  });

  it("Bounty (gain only): mover gains, opponent unaffected: Bounty_Capture_OnlyRewardsCaptor", () => {
    const result = computeCostPreview(
      pawnTradeFen,
      "e4",
      "d5",
      captureOnlyEconomy(true, false),
    );
    expect(result.moverNet).toBe(5);
    expect(result.opponentLoss).toBe(0);
  });

  it("Burn (loss only): mover gains nothing, opponent still loses: Burn_EqualTrade_DrainsBothClocksButKeepsGap", () => {
    const result = computeCostPreview(
      pawnTradeFen,
      "e4",
      "d5",
      captureOnlyEconomy(false, true),
    );
    expect(result.moverNet).toBe(0);
    expect(result.opponentLoss).toBe(5);
  });

  it("scales with the captured piece's value: CaptureValue_ScalesWithCapturedPiece", () => {
    // White knight e5 captures the black queen on d7 (value 5).
    const fen = "4k3/3q4/8/4N3/8/8/8/4K3 w - - 0 1";
    const result = computeCostPreview(
      fen,
      "e5",
      "d7",
      captureOnlyEconomy(true, true),
    );
    expect(result.victimType).toBe("q");
    expect(result.moverNet).toBe(25); // 5 * 5
    expect(result.opponentLoss).toBe(25);
  });

  it("en passant prices as a pawn capture: EnPassant_AppliesPawnValueEconomy", () => {
    // Black has just played d7-d5; white captures en passant with exd6.
    const fen = "4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1";
    const result = computeCostPreview(
      fen,
      "e5",
      "d6",
      captureOnlyEconomy(true, true),
    );
    expect(result.capture).toBe(true);
    expect(result.victimType).toBe("p");
    expect(result.moverNet).toBe(5);
  });

  it("under Burn with a real toll mode, the mover pays only the toll and never profits: Burn_Capture_MoverPaysOnlyTheToll_VictimLosesValue", () => {
    // The burn2 pool: captureCaptorGainEnabled=false, captureVictimLossEnabled=true,
    // captureMult=10,
    // moveCostMode "PerPiece" (config/kirichess.rules.json): the real preset, not an isolated
    // fixture, so this also exercises the toll branch alongside the capture branch.
    const economy = economyFromPreset(PRESETS.burn2);
    expect(economy.captureCaptorGainEnabled).toBe(false);
    expect(economy.captureVictimLossEnabled).toBe(true);

    const fen = "4k3/3q4/8/4N3/8/8/8/4K3 w - - 0 1";
    const result = computeCostPreview(fen, "e5", "d7", economy);

    const toll = economy.moveCostPerPiece.knight * economy.economyScale;
    expect(result.moverNet).toBe(-toll); // no CaptureGain event, only the movement toll
    expect(result.opponentLoss).toBe(
      economy.pieceValues.queen * economy.captureMult * economy.economyScale,
    );
  });
});

describe("computeCostPreview: movement toll (oracle: rules.generated.ts TOLL_TABLE, PerPiece mode)", () => {
  // Every rated pool shares the same toll table (TOLLS_ARE_UNIVERSAL), so flagfall stands in for
  // all of them.
  const economy = economyFromPreset(PRESETS.steal1);

  it("pawn toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/P7/4K3 w - - 0 1",
      "a2",
      "a3",
      economy,
    );
    expect(result.moverNet).toBe(-1);
  });

  it("knight toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/1N2K3 w - - 0 1",
      "b1",
      "c3",
      economy,
    );
    expect(result.moverNet).toBe(-2);
  });

  it("bishop toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/2B1K3 w - - 0 1",
      "c1",
      "b2",
      economy,
    );
    expect(result.moverNet).toBe(-2);
  });

  it("rook toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/R3K3 w - - 0 1",
      "a1",
      "a2",
      economy,
    );
    expect(result.moverNet).toBe(-3);
  });

  it("queen toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/3QK3 w - - 0 1",
      "d1",
      "d2",
      economy,
    );
    expect(result.moverNet).toBe(-4);
  });

  it("king toll (ordinary move, not castling)", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/4K3 w - - 0 1",
      "e1",
      "e2",
      economy,
    );
    expect(result.moverNet).toBe(-1);
  });

  it("castling charges the flat castle cost instead of the king's toll", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/4K2R w K - 0 1",
      "e1",
      "g1",
      economy,
    );
    expect(result.moverNet).toBe(-3); // castleCostFlat, not moveCostPerPiece.king
  });
});

describe("computeCostPreview: promotion", () => {
  it("is free under the default rules (PRICED_EVENTS.promotionQueenCost)", () => {
    const economy = economyFromPreset(PRESETS.steal1);
    expect(economy.promotionCostMult).toBe(0);
    // Pawn a7-a8=Q: only the pawn's own movement toll applies, promotion itself costs nothing.
    const result = computeCostPreview(
      "4k3/P7/8/8/8/8/8/4K3 w - - 0 1",
      "a7",
      "a8",
      economy,
      "q",
    );
    expect(result.moverNet).toBe(-1);
  });

  it("prices off the promoted-to piece when promotionCostMult is nonzero", () => {
    const economy: EconomyConfig = {
      ...captureOnlyEconomy(true, true),
      moveCostMode: "None",
      promotionCostMult: 1,
    };
    const result = computeCostPreview(
      "4k3/P7/8/8/8/8/8/4K3 w - - 0 1",
      "a7",
      "a8",
      economy,
      "q",
    );
    expect(result.moverNet).toBe(
      -economy.pieceValues.queen * economy.economyScale,
    );
  });
});

describe("computeCostPreview: base increment", () => {
  it("adds the increment to the mover's net and leaves the opponent's loss alone", () => {
    const economy = economyFromPreset(PRESETS.steal1);
    const fen = "4k3/8/8/8/8/8/8/1N2K3 w - - 0 1";
    const without = computeCostPreview(fen, "b1", "c3", economy);
    const withIncrement = computeCostPreview(fen, "b1", "c3", {
      ...economy,
      baseIncrement: 3,
    });

    expect(without.moverNet).toBe(-2);
    expect(withIncrement.moverNet).toBe(1); // knight toll 2, plus 3s, not scaled
    expect(withIncrement.opponentLoss).toBe(0);
  });

  it("stacks on a capture gain", () => {
    const economy = { ...captureOnlyEconomy(true, true), baseIncrement: 4 };
    const result = computeCostPreview(
      "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1",
      "e4",
      "d5",
      economy,
    );
    expect(result.moverNet).toBe(9); // pawn value 1 * captureMult 5, plus 4s
    expect(result.opponentLoss).toBe(5);
  });

  it("does not invent an increment when the from-square is empty", () => {
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/4K3 w - - 0 1",
      "a2",
      "a3",
      { ...captureOnlyEconomy(true, true), baseIncrement: 5 },
    );
    expect(result.moverNet).toBe(0);
  });
});

describe("computeCostPreview: check chip flag", () => {
  it("is true whenever the economy prices checks at all", () => {
    const economy = economyFromPreset(PRESETS.steal1);
    expect(economy.checkCheckerGain).toBeGreaterThan(0);
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/P7/4K3 w - - 0 1",
      "a2",
      "a3",
      economy,
    );
    expect(result.hasCheckChip).toBe(true);
  });

  it("is false when neither the check gain nor the check loss is priced", () => {
    const economy: EconomyConfig = {
      ...captureOnlyEconomy(true, true),
      checkCheckerGain: 0,
      checkVictimLoss: 0,
    };
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/P7/4K3 w - - 0 1",
      "a2",
      "a3",
      economy,
    );
    expect(result.hasCheckChip).toBe(false);
  });
});

describe("computeCostPreview: no piece at the from-square", () => {
  it("returns a zeroed result instead of throwing", () => {
    const economy = economyFromPreset(PRESETS.steal1);
    const result = computeCostPreview(
      "4k3/8/8/8/8/8/8/4K3 w - - 0 1",
      "a2",
      "a3",
      economy,
    );
    expect(result).toEqual({
      moverNet: 0,
      capture: false,
      victimType: null,
      opponentLoss: 0,
      hasCheckChip: false,
    });
  });
});

describe("movementToll", () => {
  const base = captureOnlyEconomy(true, true);

  it("None mode is always free", () => {
    expect(movementToll({ ...base, moveCostMode: "None" }, "q", 5)).toBe(0);
  });

  it("Flat mode ignores piece and distance", () => {
    const economy = { ...base, moveCostMode: "Flat" as const, moveCostFlat: 7 };
    expect(movementToll(economy, "p", 1)).toBe(7);
    expect(movementToll(economy, "q", 6)).toBe(7);
  });

  it("PerPiece mode ignores distance", () => {
    const economy = { ...base, moveCostMode: "PerPiece" as const };
    expect(movementToll(economy, "r", 1)).toBe(economy.moveCostPerPiece.rook);
    expect(movementToll(economy, "r", 7)).toBe(economy.moveCostPerPiece.rook);
  });

  it("Distance mode ignores piece and scales with distance", () => {
    const economy = {
      ...base,
      moveCostMode: "Distance" as const,
      moveCostPerSquare: 2,
    };
    expect(movementToll(economy, "p", 3)).toBe(6);
    expect(movementToll(economy, "q", 3)).toBe(6);
  });

  it("PerPieceXDistance multiplies the per-piece cost by distance", () => {
    const economy = { ...base, moveCostMode: "PerPieceXDistance" as const };
    expect(movementToll(economy, "b", 4)).toBe(
      economy.moveCostPerPiece.bishop * 4,
    );
  });
});
