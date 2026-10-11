import { Chess } from "chess.js";
import { describe, expect, it } from "vitest";
import { checkChipDeltas } from "./checkChip";
import { computeCostPreview, economyFromPreset } from "./costPreview";
import { PRESETS } from "./presets";
import { POOLS } from "./rules.generated";
import {
  TUTORIAL_STEPS,
  formatSignedSeconds,
  remainingAfterThink,
  type MoveInfo,
} from "./tutorial";

/**
 * P-2-2: the tutorial's own header comment promises every FEN and every scripted move was
 * verified with chess.js before being committed. This file is that verification, pinned as a
 * regression test rather than a one-off scratch script: it drives the actual TUTORIAL_STEPS data
 * through chess.js and computeCostPreview the same way app/[locale]/learn/page.tsx does, so a
 * future edit to a step's FEN or scriptedUci that breaks the lesson fails here instead of only
 * being discoverable by clicking through the tutorial by hand.
 */

function stepById(id: string) {
  const step = TUTORIAL_STEPS.find((s) => s.id === id);
  if (!step) throw new Error(`no tutorial step with id ${id}`);
  return step;
}

/** Small stand-in translator: returns the key plus every value it was called with, as "k=v" pairs,
 * so assertions can check which values a generatePayoff call actually produced without depending
 * on English copy (the real strings live in messages/en.json and are exercised by
 * locales.test.ts). */
const t = (key: string, values?: Record<string, string | number>) =>
  `[${key}] ` +
  Object.entries(values ?? {})
    .map(([k, v]) => `${k}=${v}`)
    .join(" ");

describe("tutorial: step data is internally consistent", () => {
  it("has one entry per rule with no duplicate ids", () => {
    const ids = TUTORIAL_STEPS.map((s) => s.id);
    expect(new Set(ids).size).toBe(ids.length);
    // P-2-2: all four rules present, not just capture economy + movement toll.
    expect(ids).toContain("captures-steal");
    expect(ids).toContain("captures-burn");
    expect(ids).toContain("captures-bounty");
    expect(ids).toContain("check-chip");
    expect(ids).toContain("repetition-nullifies");
  });

  it("gives every board step a FEN chess.js accepts", () => {
    for (const step of TUTORIAL_STEPS) {
      if (step.fen === null) continue;
      expect(() => new Chess(step.fen as string), step.id).not.toThrow();
    }
  });

  it("every scripted move is legal at the ply it's offered", () => {
    for (const step of TUTORIAL_STEPS) {
      if (!step.scriptedUci) continue;
      const chess = new Chess(step.fen as string);
      for (const uci of step.scriptedUci) {
        const legalNow = chess
          .moves({ verbose: true })
          .map((m) => m.from + m.to + (m.promotion ?? ""));
        expect(legalNow, `${step.id}: ${uci}`).toContain(uci);
        chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
      }
    }
  });
});

describe("tutorial: check-chip step exercises the real check-chip path", () => {
  const step = stepById("check-chip");
  const uci = step.scriptedUci![0];

  it("Ra1-a8+ is check but not mate, with exactly one Black reply", () => {
    const chess = new Chess(step.fen as string);
    chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
    expect(chess.isCheck()).toBe(true);
    expect(chess.isCheckmate()).toBe(false);
    expect(chess.moves()).toHaveLength(1);
  });

  it("nets the same moverDelta/opponentDelta learn/page.tsx's handleMove would compute", () => {
    const economy = economyFromPreset(PRESETS[step.preset]);
    const preview = computeCostPreview(
      step.fen as string,
      uci.slice(0, 2) as never,
      uci.slice(2, 4) as never,
      economy,
    );
    const chess = new Chess(step.fen as string);
    chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
    const chip = checkChipDeltas(chess, POOLS[step.preset].config);
    // Mirrors handleMove: preview.moverNet/opponentLoss plus checkChipDeltas. This step is a
    // single check, so the double-check multiplier does not change the chip.
    const moverDelta = preview.moverNet + chip.mover;
    const opponentDelta = -preview.opponentLoss + chip.opponent;

    // Rook toll (3) minus the check-checker chip (2): a net cost, not a windfall, which is the
    // point of using a rook here rather than a cheaper piece.
    expect(moverDelta).toBe(-1);
    expect(opponentDelta).toBe(-2);

    const info: MoveInfo = {
      piece: "r",
      isCheck: true,
      moverDelta,
      opponentDelta,
    };
    const payoff = step.generatePayoff!(info, t);
    expect(payoff).toContain("gain=-1s");
    expect(payoff).toContain("loss=-2s");
  });
});

describe("tutorial: repetition step exercises the real Rule 4 nullification", () => {
  const step = stepById("repetition-nullifies");
  const sequence = step.scriptedUci!;

  it("the four-ply knight shuffle returns to the exact starting position", () => {
    const chess = new Chess(step.fen as string);
    const startKey = (chess.fen() as string).split(" ").slice(0, 4).join(" ");
    for (const uci of sequence) {
      chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
    }
    const endKey = chess.fen().split(" ").slice(0, 4).join(" ");
    expect(endKey).toBe(startKey);
    // Only the halfmove/fullmove counters should differ, exactly what the engine's own
    // PositionKey ignores (KiriChessEngine.cs, PositionKey).
    expect(chess.fen()).not.toBe(step.fen);
  });

  it("nullifiesLastMove is set, and only the final ply is the repeat", () => {
    expect(step.nullifiesLastMove).toBe(true);
    expect(sequence).toHaveLength(4);
  });

  it("the final ply's would-be economy is a plain knight toll, and generatePayoff reports it cancelled", () => {
    const chess = new Chess(step.fen as string);
    for (const uci of sequence.slice(0, 3)) {
      chess.move({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
    }
    const finalUci = sequence[3];
    const economy = economyFromPreset(PRESETS[step.preset]);
    const fenBeforeFinalPly = chess.fen();
    const preview = computeCostPreview(
      fenBeforeFinalPly,
      finalUci.slice(0, 2) as never,
      finalUci.slice(2, 4) as never,
      economy,
    );
    // Plain knight move, no capture, no check: the only cost is the movement toll.
    expect(preview.capture).toBe(false);
    expect(preview.moverNet).toBe(
      -POOLS[step.preset].config.moveCostPerPiece.knight,
    );

    const info: MoveInfo = {
      piece: "n",
      isCheck: false,
      moverDelta: 0,
      opponentDelta: 0,
      nullified: true,
      wouldBeMoverDelta: preview.moverNet,
      wouldBeOpponentDelta: 0,
    };
    const payoff = step.generatePayoff!(info, t);
    expect(payoff).toContain(
      `wouldBe=${formatSignedSeconds(preview.moverNet)}`,
    );
    expect(payoff).toContain("actual=0s");
  });

  it("a non-final ply reports the ordinary shuffle payoff, not the nullified one", () => {
    const info: MoveInfo = {
      piece: "n",
      isCheck: false,
      moverDelta: -2,
      opponentDelta: 0,
      nullified: false,
      plyNumber: 1,
      plyTotal: 4,
    };
    const payoff = step.generatePayoff!(info, t);
    expect(payoff).toContain("step=1");
    expect(payoff).toContain("total=4");
    expect(payoff).toContain("cost=-2s");
  });
});

describe("tutorial: Burn and Bounty each price the same knight trade on their own board", () => {
  const from = "c3";
  const to = "d5"; // Nc3xd5
  const burn = stepById("captures-burn");
  const bounty = stepById("captures-bounty");

  it("uses the same position and each step's own preset", () => {
    expect(burn.fen).toBe(bounty.fen);
    expect(burn.preset).toBe("burn1");
    expect(bounty.preset).toBe("bounty1");
  });

  it("Burn pays only the toll and takes the piece's time off the victim", () => {
    const preview = computeCostPreview(
      burn.fen as string,
      from as never,
      to as never,
      economyFromPreset(PRESETS.burn1),
    );
    expect(preview.capture).toBe(true);
    expect(preview.moverNet).toBe(
      -POOLS.burn1.config.moveCostPerPiece.knight,
    );
    expect(preview.opponentLoss).toBeGreaterThan(0);

    const payoff = burn.generatePayoff!(
      {
        piece: "n",
        captured: "n",
        isCheck: false,
        moverDelta: preview.moverNet,
        opponentDelta: -preview.opponentLoss,
      },
      t,
    );
    expect(payoff).toContain(`net=${formatSignedSeconds(preview.moverNet)}`);
    expect(payoff).toContain(
      `loss=${formatSignedSeconds(-preview.opponentLoss)}`,
    );
  });

  it("Bounty pays the capture to the mover and leaves the victim's clock alone", () => {
    const preview = computeCostPreview(
      bounty.fen as string,
      from as never,
      to as never,
      economyFromPreset(PRESETS.bounty1),
    );
    expect(preview.capture).toBe(true);
    expect(preview.opponentLoss).toBe(0);
    expect(preview.moverNet).toBeGreaterThan(0);

    const payoff = bounty.generatePayoff!(
      {
        piece: "n",
        captured: "n",
        isCheck: false,
        moverDelta: preview.moverNet,
        opponentDelta: 0,
      },
      t,
    );
    expect(payoff).toContain(`gain=${formatSignedSeconds(preview.moverNet)}`);
  });
});

describe("tutorial: remainingAfterThink (P-2-4)", () => {
  it("leaves both clocks alone when the step is not ticking", () => {
    expect(
      remainingAfterThink(360, 360, "White", 5_000, false),
    ).toEqual({ white: 360, black: 360 });
  });

  it("subtracts elapsed think time from the side to move", () => {
    expect(
      remainingAfterThink(360, 180, "White", 2_500, true),
    ).toEqual({ white: 357.5, black: 180 });
    expect(
      remainingAfterThink(360, 180, "Black", 1_000, true),
    ).toEqual({ white: 360, black: 179 });
  });

  it("clamps a flag-fall at zero rather than going negative", () => {
    expect(
      remainingAfterThink(1, 60, "White", 4_000, true),
    ).toEqual({ white: 0, black: 60 });
  });
});
