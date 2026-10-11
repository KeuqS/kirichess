import { describe, expect, it } from "vitest";
import { capturesAlongLine, START_FEN } from "./fen";

/**
 * `capturesAlongLine` exists so the play page can rebuild its captured-material panel from a move
 * log that carries moves but not victims (C-1-2). The three cases below are the ones that make it
 * more than a lookup: each leaves a piece somewhere the naive "from -> to" reading does not, and
 * each would make a *later* ply name the wrong victim.
 */
describe("capturesAlongLine", () => {
  it("reports null for every quiet move", () => {
    expect(capturesAlongLine(["e2e4", "e7e5", "g1f3", "b8c6"])).toEqual([
      null,
      null,
      null,
      null,
    ]);
  });

  it("names the piece standing on the target square", () => {
    // 1. e4 d5 2. exd5 Qxd5 3. Nc3 Qxa2 4. Rxa2
    expect(
      capturesAlongLine([
        "e2e4",
        "d7d5",
        "e4d5",
        "d8d5",
        "b1c3",
        "d5a2",
        "a1a2",
      ]),
    ).toEqual([null, null, "p", "p", null, "p", "q"]);
  });

  it("takes the passed pawn on an en passant capture", () => {
    // 1. e4 a6 2. e5 d5 3. exd6 e.p.: the pawn removed stands on d5, not on d6.
    const victims = capturesAlongLine(["e2e4", "a7a6", "e4e5", "d7d5", "e5d6"]);
    expect(victims[4]).toBe("p");
  });

  it("moves the rook with the castling king, so a later capture on f1 finds it", () => {
    // 1. e4 e5 2. Nf3 d6 3. Bc4 Bg4 4. O-O Bxf3: the bishop takes the knight on f3, and only then
    // 5. gxf3 ... the point is that the rook is on f1, not h1, for anything that follows.
    const line = [
      "e2e4",
      "e7e5",
      "g1f3",
      "d7d6",
      "f1c4",
      "c8g4",
      "e1g1",
      "g4f3",
      "g2f3",
      "d8h4",
      // A queen landing on f1 must find the castled rook there.
      "h4f2",
      "g1f2",
      "g8f6",
      "f1g1",
    ];
    const victims = capturesAlongLine(line);
    expect(victims[7]).toBe("n"); // Bxf3, the knight
    expect(victims[8]).toBe("b"); // gxf3, the bishop that took it
    expect(victims[10]).toBe("p"); // Qxf2, the untouched f2 pawn
    expect(victims[11]).toBe("q"); // Kxf2, the queen
    expect(victims[13]).toBeNull(); // Rf1-g1: the rook really is on f1 after castling
  });

  it("carries the promoted piece forward, not the pawn", () => {
    // A bare king-and-pawn position: White promotes on b8, and Black's king takes on b8 next. The
    // victim is the queen the pawn became, not a pawn.
    const victims = capturesAlongLine(
      ["b7b8q", "a8b8"],
      "k1K5/pP6/8/8/8/8/8/8 w - - 0 1",
    );
    expect(victims).toEqual([null, "q"]);
  });

  it("starts from the standard position by default", () => {
    expect(START_FEN.startsWith("rnbqkbnr/pppppppp")).toBe(true);
    expect(capturesAlongLine(["b1c3"])).toEqual([null]);
  });
});
