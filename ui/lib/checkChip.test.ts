import { Chess } from "chess.js";
import { describe, expect, it } from "vitest";
import { checkChipDeltas, type CheckChipConfig } from "./checkChip";

/**
 * The same positions as `Rule3CheckChipTests`. That file is the engine's guard for
 * `doubleCheckChipMultiplier`; this one is the tutorial's, which prices the chip itself
 * because `/learn` has no server.
 */

function chip(multiplier: number, gain = 3, loss = 2): CheckChipConfig {
  return {
    checkCheckerGain: gain,
    checkVictimLoss: loss,
    economyScale: 1,
    doubleCheckChipMultiplier: multiplier,
  };
}

function after(fen: string, uci: string): Chess {
  const chess = new Chess(fen);
  chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
  return chess;
}

describe("checkChipDeltas", () => {
  it("pays a single check once, even when the multiplier is set", () => {
    const chess = after("4k3/8/8/8/8/8/8/R3K3 w - - 0 1", "a1a8");
    expect(chess.isCheck()).toBe(true);
    expect(chess.isCheckmate()).toBe(false);
    expect(checkChipDeltas(chess, chip(2))).toEqual({ mover: 3, opponent: -2 });
  });

  it("multiplies both halves when the move is a double check", () => {
    // Ne4-d6 checks and discovers the e1 rook: the engine's DoubleCheck_MultipliesBothHalvesOfTheChip.
    const chess = after("4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1", "e4d6");
    expect(chess.isCheck()).toBe(true);
    expect(chess.isCheckmate()).toBe(false);
    expect(checkChipDeltas(chess, chip(2))).toEqual({ mover: 6, opponent: -4 });
  });

  it("still doubles when one checker is pinned against its own king", () => {
    // The e1 rook is pinned by the black rook on a1 and still counts. FIDE: a pin limits
    // where a piece may move, not what it attacks.
    const chess = after("4k3/8/8/8/4N3/8/8/r3R2K w - - 0 1", "e4d6");
    expect(checkChipDeltas(chess, chip(2))).toEqual({ mover: 6, opponent: -4 });
  });

  it("pays the single-check chip when the multiplier is 1", () => {
    const chess = after("4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1", "e4d6");
    expect(checkChipDeltas(chess, chip(1))).toEqual({ mover: 3, opponent: -2 });
  });

  it("scales by economyScale before the multiplier", () => {
    const chess = after("4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1", "e4d6");
    expect(
      checkChipDeltas(chess, { ...chip(2), economyScale: 2 }),
    ).toEqual({ mover: 12, opponent: -8 });
  });

  it("stays zero when neither half of the chip is priced", () => {
    const chess = after("4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1", "e4d6");
    expect(checkChipDeltas(chess, chip(2, 0, 0))).toEqual({
      mover: 0,
      opponent: 0,
    });
  });

  it("pays nothing for a checkmate", () => {
    // Qg5-g7#. The white king on f6 protects the queen; g6 is empty so the queen's path is clear.
    const chess = after("7k/8/5K2/6Q1/8/8/8/8 w - - 0 1", "g5g7");
    expect(chess.isCheckmate()).toBe(true);
    expect(checkChipDeltas(chess, chip(2))).toEqual({ mover: 0, opponent: 0 });
  });

  it("pays nothing for a quiet move", () => {
    const chess = after("4k3/8/8/8/8/8/8/R3K3 w - - 0 1", "a1b1");
    expect(chess.isCheck()).toBe(false);
    expect(checkChipDeltas(chess, chip(2))).toEqual({ mover: 0, opponent: 0 });
  });
});
