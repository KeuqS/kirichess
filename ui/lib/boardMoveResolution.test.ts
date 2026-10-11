import { describe, expect, it } from "vitest";
import {
  promotionChoices,
  promotionPickerLayout,
  resolveBoardMove,
} from "./boardMoveResolution";

/** King e1, rooks a1 and h1, nothing between them: both castles are on the board. */
const CASTLE_FEN = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
/** White pawn one step from promoting; black king far away so it never blocks anything. */
const WHITE_PROMOTION_FEN = "8/P6k/8/8/8/8/8/4K3 w - - 0 1";
/** Black pawn one step from promoting, mirrored for the black-to-move / rank-1 case. */
const BLACK_PROMOTION_FEN = "4k3/8/8/8/8/8/p6K/8 b - - 0 1";
const START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

describe("resolveBoardMove - click path (no dropped piece)", () => {
  it("returns the uci for a legal, non-special move", () => {
    expect(resolveBoardMove(START_FEN, ["e2e4", "e2e3"], "e2", "e4")).toBe(
      "e2e4",
    );
  });

  it("silently rejects an illegal target: returns null, not a wrong move", () => {
    expect(
      resolveBoardMove(START_FEN, ["e2e4", "e2e3"], "e2", "e5"),
    ).toBeNull();
  });

  it("does not pick a piece when more than one promotion is legal", () => {
    // The click path opens a picker for this. Inventing a queen here is the bug C-11-6 closes.
    expect(
      resolveBoardMove(
        WHITE_PROMOTION_FEN,
        ["a7a8q", "a7a8r", "a7a8b", "a7a8n"],
        "a7",
        "a8",
      ),
    ).toBeNull();
    expect(
      resolveBoardMove(WHITE_PROMOTION_FEN, ["a7a8r", "a7a8n"], "a7", "a8"),
    ).toBeNull();
  });

  it("plays the only legal promotion, including an under-promotion", () => {
    expect(
      resolveBoardMove(WHITE_PROMOTION_FEN, ["a7a8q"], "a7", "a8"),
    ).toBe("a7a8q");
    // Queen would be self-check; the click must still play the knight rather than do nothing.
    expect(
      resolveBoardMove(WHITE_PROMOTION_FEN, ["a7a8n"], "a7", "a8"),
    ).toBe("a7a8n");
  });

  it("does not pick a piece for a black promotion with several choices", () => {
    expect(
      resolveBoardMove(
        BLACK_PROMOTION_FEN,
        ["a2a1q", "a2a1r", "a2a1b", "a2a1n"],
        "a2",
        "a1",
      ),
    ).toBeNull();
  });

  it("plays a black promotion when only one piece is legal", () => {
    expect(
      resolveBoardMove(BLACK_PROMOTION_FEN, ["a2a1r"], "a2", "a1"),
    ).toBe("a2a1r");
  });

  it("castles kingside when the king's square is selected and the h-file rook is clicked", () => {
    const uci = resolveBoardMove(CASTLE_FEN, ["e1g1", "e1e2"], "e1", "h1");
    expect(uci).toBe("e1g1");
  });

  it("castles queenside when the king's square is selected and the a-file rook is clicked", () => {
    const uci = resolveBoardMove(CASTLE_FEN, ["e1c1", "e1e2"], "e1", "a1");
    expect(uci).toBe("e1c1");
  });

  it("does not invent a castle when it isn't actually legal (e.g. king already moved)", () => {
    // Position still has a king next to a same-side rook, but the castling right is off the
    // table (e1g1 not in legalUci) - clicking the rook must not force the move through.
    expect(resolveBoardMove(CASTLE_FEN, ["e1e2"], "e1", "h1")).toBeNull();
  });

  it("falls back to a plain move check when the rook isn't on a castling file", () => {
    // Same king + same-side rook shape, but the rook sits on d1: not a or h, so there is no
    // castling notation to translate to, and a normal king move onto its own rook (not present
    // in legalUci here, the way the real engine would never offer it) is rejected.
    const fen = "4k3/8/8/8/8/8/8/3RK3 w - - 0 1";
    expect(resolveBoardMove(fen, ["e1e2"], "e1", "d1")).toBeNull();
  });
});

describe("promotionChoices", () => {
  const all = ["a7a8n", "a7a8b", "a7a8r", "a7a8q"];

  it("returns pieces in picker order, ignoring the order of the legal list", () => {
    expect(promotionChoices(all, "a7", "a8")).toEqual(["q", "r", "b", "n"]);
  });

  it("keeps only the pieces the position allows, still in picker order", () => {
    expect(promotionChoices(["a7a8n", "a7a8r"], "a7", "a8")).toEqual(["r", "n"]);
  });

  it("is empty for a move that is not a promotion", () => {
    expect(promotionChoices(["e2e4", "e2e3"], "e2", "e4")).toEqual([]);
  });
});

describe("promotionPickerLayout", () => {
  it("grows down the file from white's promotion rank", () => {
    expect(promotionPickerLayout("a8", "white", 4)).toEqual({
      squares: ["a8", "a7", "a6", "a5"],
      growsDown: true,
    });
  });

  it("grows up the file from black's promotion rank on a white-oriented board", () => {
    expect(promotionPickerLayout("h1", "white", 4)).toEqual({
      squares: ["h1", "h2", "h3", "h4"],
      growsDown: false,
    });
  });

  it("flips with the board, so black's promotion rank is the top edge", () => {
    expect(promotionPickerLayout("a1", "black", 2)).toEqual({
      squares: ["a1", "a2"],
      growsDown: true,
    });
    expect(promotionPickerLayout("a8", "black", 2)).toEqual({
      squares: ["a8", "a7"],
      growsDown: false,
    });
  });
});

describe("resolveBoardMove - drag-and-drop path (dropped piece given)", () => {
  it("returns the uci for a legal, non-promotion drop", () => {
    expect(
      resolveBoardMove(START_FEN, ["e2e4", "e2e3"], "e2", "e4", "wP"),
    ).toBe("e2e4");
  });

  it("silently rejects an illegal target: returns null", () => {
    expect(
      resolveBoardMove(START_FEN, ["e2e4", "e2e3"], "e2", "e5", "wP"),
    ).toBeNull();
  });

  it("takes the promotion piece from the dropped glyph, not a queen default", () => {
    const uci = resolveBoardMove(
      WHITE_PROMOTION_FEN,
      ["a7a8q", "a7a8r", "a7a8b", "a7a8n"],
      "a7",
      "a8",
      "wN",
    );
    expect(uci).toBe("a7a8n");
  });

  it("respects a dropped rook or bishop the same way", () => {
    const legal = ["a7a8q", "a7a8r", "a7a8b", "a7a8n"];
    expect(resolveBoardMove(WHITE_PROMOTION_FEN, legal, "a7", "a8", "wR")).toBe(
      "a7a8r",
    );
    expect(resolveBoardMove(WHITE_PROMOTION_FEN, legal, "a7", "a8", "wB")).toBe(
      "a7a8b",
    );
  });

  it("rejects a promotion piece the position doesn't actually allow", () => {
    expect(
      resolveBoardMove(WHITE_PROMOTION_FEN, ["a7a8q"], "a7", "a8", "wN"),
    ).toBeNull();
  });

  it("has no castle-by-drop-on-rook affordance: dropping the king on its rook is just illegal", () => {
    // Unlike the click path, a drop never translates this into e1g1 - it only ever checks the
    // literal source+target square pair.
    expect(
      resolveBoardMove(CASTLE_FEN, ["e1g1", "e1e2"], "e1", "h1", "wK"),
    ).toBeNull();
  });
});
