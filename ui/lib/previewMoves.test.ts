import { describe, expect, it } from "vitest";
import { previewUci } from "./previewMoves";

const START = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
/** Black to move after 1.e4. White's e-pawn can still step to e5 in this placement. */
const AFTER_E4 = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

describe("previewUci", () => {
  it("lists the side to move when the FEN already matches", () => {
    const fromE2 = previewUci(START, "w").filter((u) => u.startsWith("e2"));
    expect(fromE2).toEqual(expect.arrayContaining(["e2e3", "e2e4"]));
  });

  it("lists the waiting side's moves and ignores the opponent's en passant square", () => {
    const white = previewUci(AFTER_E4, "w");
    expect(white.filter((u) => u.startsWith("e4"))).toEqual(["e4e5"]);
    expect(white.some((u) => u.startsWith("e7"))).toBe(false);
  });

  it("still lists the waiting side when the opponent is in check", () => {
    // Queen on e5 checks the black king. Flipping the turn is an illegal FEN
    // (the side not to move is in check) unless validation is skipped.
    const checked = "4k3/8/8/4Q3/8/8/4P3/4K3 b - - 0 1";
    expect(previewUci(checked, "w").filter((u) => u.startsWith("e2"))).toEqual([
      "e2e3",
      "e2e4",
    ]);
  });
});
