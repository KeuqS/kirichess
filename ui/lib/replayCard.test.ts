import { describe, expect, it } from "vitest";
import { replayCardSwing, replayCardText } from "./replayCard";
import type { EconomyConfig, ReplayPlyDto, ReplayResponse } from "./types";

/** Mirrors clockAnalysis.test.ts's makePly: only fields relevant to a test are ever varied. */
function makePly(
  overrides: Partial<ReplayPlyDto> & { ply: number },
): ReplayPlyDto {
  return {
    uci: "e2e4",
    san: "e4",
    mover: "White",
    fenAfter: "startpos",
    whiteTimeAfter: 0,
    blackTimeAfter: 0,
    whiteDelta: 0,
    blackDelta: 0,
    events: [],
    ...overrides,
  };
}

function makeEconomy(startTimeSeconds: number): EconomyConfig {
  return {
    startTimeSeconds,
    economyScale: 1,
    pieceValues: { pawn: 1, knight: 3, bishop: 3, rook: 5, queen: 9 },
    captureMult: 1,
    captureCaptorGainEnabled: true,
    captureVictimLossEnabled: true,
    moveCostMode: "Flat",
    moveCostPerPiece: {
      pawn: 1,
      knight: 1,
      bishop: 1,
      rook: 1,
      queen: 1,
      king: 1,
    },
    castleCostFlat: 0,
    moveCostFlat: 1,
    moveCostPerSquare: 0,
    promotionCostMult: 1,
    checkCheckerGain: 0,
    checkVictimLoss: 0,
    doubleCheckChipMultiplier: 1,
    baseIncrement: 0,
    repeatedPositionNullifiesTime: true,
    threefoldRepetitionIsDraw: true,
    flagFallLoses: true,
    flagVsInsufficientMaterialIsDraw: true,
  };
}

function makeReplay(overrides: Partial<ReplayResponse> = {}): ReplayResponse {
  return {
    gameId: "11111111-1111-1111-1111-111111111111",
    startFen: "startpos",
    whitePlayerId: "22222222-2222-2222-2222-222222222222",
    blackPlayerId: "33333333-3333-3333-3333-333333333333",
    whitePlayerName: "Alice",
    blackPlayerName: "Bob",
    whitePatronTier: "None",
    blackPatronTier: "None",
    result: "WhiteWins",
    termination: "Checkmate",
    ratingPool: "steal1",
    isRated: true,
    createdAt: "2026-01-01T00:00:00Z",
    endedAt: "2026-01-01T00:10:00Z",
    economy: makeEconomy(360),
    plies: [],
    truncatedAtPly: null,
    ...overrides,
  };
}

describe("replayCardText", () => {
  it("names both players and reports result/termination/ply count", () => {
    const replay = makeReplay({
      plies: [makePly({ ply: 1 }), makePly({ ply: 2, mover: "Black" })],
    });
    const { title, description } = replayCardText(replay);
    expect(title).toBe("Alice vs Bob");
    expect(description).toBe(
      "White wins by checkmate, 2 plies. Watch the replay on KiriChess.",
    );
  });

  it("says the stored record is incomplete when playback was truncated", () => {
    const replay = makeReplay({
      truncatedAtPly: 2,
      plies: [makePly({ ply: 1 })],
    });
    expect(replayCardText(replay).description).toContain(
      "The stored record is incomplete.",
    );
  });

  it("singularizes ply count for a one-move game", () => {
    const replay = makeReplay({ plies: [makePly({ ply: 1 })] });
    expect(replayCardText(replay).description).toContain("1 ply");
  });
});

describe("replayCardSwing", () => {
  it("returns null for a game with no ply clearing the swing threshold", () => {
    const replay = makeReplay({
      plies: [makePly({ ply: 1, whiteDelta: -1, blackDelta: -2 })], // gapDelta 1, well under threshold 18
    });
    expect(replayCardSwing(replay)).toBeNull();
  });

  it("returns null for an empty game", () => {
    expect(replayCardSwing(makeReplay({ plies: [] }))).toBeNull();
  });

  it("picks the single biggest swing, names the favored player, and carries the SAN and ply", () => {
    const replay = makeReplay({
      plies: [
        makePly({ ply: 1, san: "e4", whiteDelta: 0, blackDelta: -20 }), // magnitude 20, favors White
        makePly({
          ply: 2,
          san: "Qxh7+",
          mover: "Black",
          whiteDelta: -40,
          blackDelta: 0,
        }), // magnitude 40, favors Black
      ],
    });
    const swing = replayCardSwing(replay);
    expect(swing).toEqual({
      ply: 2,
      san: "Qxh7+",
      magnitude: 40,
      favoredName: "Bob",
    });
  });

  it("falls back to the uci when san is empty", () => {
    const replay = makeReplay({
      plies: [
        makePly({
          ply: 1,
          san: "",
          uci: "h2h7",
          whiteDelta: 30,
          blackDelta: 0,
        }),
      ],
    });
    expect(replayCardSwing(replay)?.san).toBe("h2h7");
  });
});
