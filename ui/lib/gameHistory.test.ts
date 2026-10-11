import { describe, expect, it } from "vitest";
import { historyFromSnapshot } from "./gameHistory";
import { captureBaseSeconds } from "./costPreview";
import { presetToCustomConfig } from "./customConfig";
import type { EconomyConfig, GameSnapshot, SpectatorMoveDto } from "./types";

const ECONOMY = presetToCustomConfig("bounty1") as EconomyConfig;

function ply(
  index: number,
  uci: string,
  overrides: Partial<SpectatorMoveDto> = {},
): SpectatorMoveDto {
  return {
    ply: index,
    uci,
    mover: index % 2 === 1 ? "White" : "Black",
    whiteTimeAfter: 300 - index,
    blackTimeAfter: 300 - index,
    whiteDelta: 0,
    blackDelta: 0,
    ...overrides,
  };
}

function snapshot(moves: SpectatorMoveDto[]): GameSnapshot {
  return {
    state: {
      gameId: "11111111-1111-1111-1111-111111111111",
      fen: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
      sideToMove: moves.length % 2 === 0 ? "White" : "Black",
      // Deliberately not the starting clock: on a rejoin these are what the player has *left*, and
      // the clock graph must still open at the configured start.
      whiteTime: 123,
      blackTime: 117,
      status: "Ongoing",
      winner: null,
      termination: "None",
      whitePlayerId: "22222222-2222-2222-2222-222222222222",
      blackPlayerId: "33333333-3333-3333-3333-333333333333",
      moveCount: moves.length,
      economy: ECONOMY,
      rated: true,
      activeClockOffsetMs: 0,
      whiteViaApi: false,
      blackViaApi: false,
    },
    legalMoves: ["e2e4"],
    moves,
  };
}

describe("historyFromSnapshot", () => {
  it("numbers moves by the server's ply, not by however many the client happens to hold", () => {
    const history = historyFromSnapshot(
      snapshot([ply(1, "e2e4"), ply(2, "e7e5"), ply(3, "g1f3")]),
    );

    expect(history.moves.map((m) => m.moveNumber)).toEqual([1, 2, 3]);
    expect(history.moves.map((m) => m.side)).toEqual([
      "White",
      "Black",
      "White",
    ]);
    expect(history.moves.map((m) => m.uci)).toEqual(["e2e4", "e7e5", "g1f3"]);
  });

  it("rebuilds rather than appends, so a second join after missed plies is exact (C-1-2)", () => {
    // The reconnect the finding is about: the client joined at ply 2, three more plies landed while
    // its transport was down, and the rejoin snapshot carries all five. The rebuild must produce
    // five entries numbered 1..5 - not the two it held plus three appended at 3..5 off a stale
    // length, and not the two it held with the middle silently missing.
    const firstJoin = historyFromSnapshot(
      snapshot([ply(1, "e2e4"), ply(2, "e7e5")]),
    );
    expect(firstJoin.moves).toHaveLength(2);

    const rejoin = historyFromSnapshot(
      snapshot([
        ply(1, "e2e4"),
        ply(2, "e7e5"),
        ply(3, "g1f3"),
        ply(4, "b8c6"),
        ply(5, "f1c4"),
      ]),
    );

    expect(rejoin.moves).toHaveLength(5);
    expect(rejoin.moves.map((m) => m.moveNumber)).toEqual([1, 2, 3, 4, 5]);
    expect(rejoin.moves.at(-1)?.uci).toBe("f1c4");
  });

  it("is idempotent: the same snapshot rebuilds to the same history", () => {
    const snap = snapshot([ply(1, "e2e4"), ply(2, "d7d5"), ply(3, "e4d5")]);
    expect(historyFromSnapshot(snap)).toEqual(historyFromSnapshot(snap));
  });

  it("opens the clock graph at the configured start, not at the current clock", () => {
    const history = historyFromSnapshot(
      snapshot([ply(1, "e2e4"), ply(2, "e7e5")]),
    );

    expect(history.clockHistory).toHaveLength(3);
    expect(history.clockHistory[0]).toEqual({
      moveNumber: 0,
      whiteTime: ECONOMY.startTimeSeconds,
      blackTime: ECONOMY.startTimeSeconds,
    });
    expect(history.clockHistory.map((p) => p.moveNumber)).toEqual([0, 1, 2]);
    expect(history.clockHistory[2].whiteTime).toBe(298);
  });

  it("recovers captured material and the seconds those captures won", () => {
    // 1. e4 d5 2. exd5 Qxd5 3. Nc3 Qxa2: White has taken a pawn, Black a pawn and a pawn.
    const history = historyFromSnapshot(
      snapshot([
        ply(1, "e2e4"),
        ply(2, "d7d5"),
        ply(3, "e4d5"),
        ply(4, "d8d5"),
        ply(5, "b1c3"),
        ply(6, "d5a2"),
      ]),
    );

    expect(history.capturedByWhite).toEqual(["p"]);
    expect(history.capturedByBlack).toEqual(["p", "p"]);
    expect(history.captureSecondsWhite).toBe(captureBaseSeconds(ECONOMY, "p"));
    expect(history.captureSecondsBlack).toBe(
      2 * captureBaseSeconds(ECONOMY, "p"),
    );
  });

  it("wins no seconds from a capture in a pool where the captor does not gain", () => {
    const noGain: EconomyConfig = {
      ...ECONOMY,
      captureCaptorGainEnabled: false,
    };
    const snap = snapshot([ply(1, "e2e4"), ply(2, "d7d5"), ply(3, "e4d5")]);
    const history = historyFromSnapshot({
      ...snap,
      state: { ...snap.state, economy: noGain },
    });

    // The piece still shows in the panel; only the "+Ns" total is silent, matching the server,
    // which emits no CaptureGain event at all in that configuration.
    expect(history.capturedByWhite).toEqual(["p"]);
    expect(history.captureSecondsWhite).toBe(0);
  });

  it("returns an empty history for a game with no plies yet", () => {
    const history = historyFromSnapshot(snapshot([]));

    expect(history.moves).toEqual([]);
    expect(history.capturedByWhite).toEqual([]);
    expect(history.capturedByBlack).toEqual([]);
    expect(history.clockHistory).toHaveLength(1);
  });
});
