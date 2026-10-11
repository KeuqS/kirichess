// The "clock is the board" onboarding tutorial (roadmap 7.5).
//
// This is a fully scripted, no-backend, no-auth walkthrough, and its legality authority is its own:
// `app/[locale]/learn/page.tsx` imports chess.js and builds `new Chess(step.fen)` for each step, and
// that instance (not the server's /legal-moves, which the real board uses) decides whether the
// player's move is legal, whether it gives check, and whether it is mate. A no-backend sandbox is
// exactly where a client-side legality check belongs; the point worth remembering is that this
// module carries only a static `fen` per step, no move list and no `fenAfter` field. Every FEN and
// every scripted move below was verified with chess.js before being committed, which is a separate
// fact from the page using chess.js live.
//
// Clock numbers are computed, not hardcoded: the page combines `computeCostPreview` (from
// `./costPreview`) with each move's economy preset to get the real moverNet/opponentLoss for a
// move. Two things that function deliberately does not derive get folded in at the call site
// (app/[locale]/learn/page.tsx) inline from `POOLS[step.preset].config`, because both are
// position-and-history-dependent rather than pure functions of the move:
//   - the check-chip amount (the "check-chip" step): `computeCostPreview` reports `hasCheckChip`
//     but not the amount, since the amount depends on the position after the move. The page calls
//     `checkChipDeltas` (`./checkChip`), which matches the engine: nothing on mate, and both halves
//     multiplied by `doubleCheckChipMultiplier` when two pieces attack the king. No step in this
//     file is a double check; the helper is what keeps a future one from showing the single-check
//     number. This step's FEN is the first one here whose scripted move is actually a check, so it
//     is the first step that exercises that path for real.
//   - repetition nullification (the "repetition-nullifies" step): Rule 4 cancels a move's entire
//     economy the moment the resulting position has been seen before in the same game, which is
//     history the tutorial has to track itself (it isn't part of a single move's cost preview).
//     `scriptedUci` pins the exact four-ply shuffle (knight out, knight out, knight back, knight
//     back) that returns the board to its own starting position, and `nullifiesLastMove` tells the
//     page that the fourth ply, specifically, is the repeat: cancel its computed delta to zero and
//     report what it would have been instead. See MoveInfo's `nullified`/`wouldBeMoverDelta` below.
//
// Phase 9f.4. Step copy lives in messages/en.json under the "learn" namespace: this module is not
// a component and can't call useTranslations itself, so it carries translation *keys* (titleKey,
// bodyKey) rather than literal English, and generatePayoff takes a translator function supplied by
// the caller (app/learn/page.tsx) at call time, the same pattern app/custom/page.tsx uses for its
// module-level validateConfig.

import type { PresetKey } from "./presets";
import { POOLS } from "./rules.generated";
import type { Side } from "./types";

/** A pool's full starting clock, for the steps that stage a game from move one. */
const fullClock = (pool: PresetKey) => POOLS[pool].config.startTimeSeconds;

export interface MoveInfo {
  piece: string;
  captured?: string;
  isCheck: boolean;
  moverDelta: number;
  opponentDelta: number;
  /** True when this move's economy was cancelled outright by the repetition guard (Rule 4). */
  nullified?: boolean;
  /** Only set when `nullified`: what moverDelta/opponentDelta would have been before cancellation. */
  wouldBeMoverDelta?: number;
  wouldBeOpponentDelta?: number;
  /** 1-based position of this move within `scriptedUci`, for steps that play more than one ply. */
  plyNumber?: number;
  plyTotal?: number;
}

export type Translator = (
  key: string,
  values?: Record<string, string | number>,
) => string;

export interface TutorialStep {
  id: string;
  titleKey: string;
  bodyKey: string;
  preset: PresetKey;
  fen: string | null;
  whiteSeconds: number;
  blackSeconds: number;
  generatePayoff?: (info: MoveInfo, t: Translator) => string;
  /** This step is about the cost of moving, not capturing: hide capture moves as legal. */
  disallowCaptures?: boolean;
  /**
   * Pins the board to this exact sequence of UCI moves, one ply at a time (only the ply at the
   * current index is offered as legal), rather than letting the player choose freely. Used for
   * steps whose lesson depends on a specific sequence happening (the repetition shuffle), or where
   * "obvious" needs to mean "the only option on the board" (the check step).
   */
  scriptedUci?: string[];
  /**
   * When set alongside `scriptedUci`, the final ply in the sequence has its computed economy
   * (toll, capture, everything) cancelled to zero, matching the engine's Rule 4: "once seen, always
   * seen." The move that would have been played without this flag is reported via
   * `wouldBeMoverDelta`/`wouldBeOpponentDelta` on that ply's MoveInfo.
   */
  nullifiesLastMove?: boolean;
}

/**
 * P-2-4: subtract think time from the side that is on move. `elapsedMs` is a monotonic delta
 * (performance.now() minus the last event), not wall-clock Date.now(), matching realtime.md.
 * Non-board steps pass `ticking: false` so the opener's 180-vs-7 demonstration stays frozen.
 */
export function remainingAfterThink(
  white: number,
  black: number,
  activeSide: Side | null,
  elapsedMs: number,
  ticking: boolean,
): { white: number; black: number } {
  if (!ticking || activeSide === null || elapsedMs <= 0) {
    return { white: Math.max(0, white), black: Math.max(0, black) };
  }
  const elapsedSec = elapsedMs / 1000;
  if (activeSide === "White") {
    return { white: Math.max(0, white - elapsedSec), black };
  }
  return { white, black: Math.max(0, black - elapsedSec) };
}

/** Translation keys for the six lowercase piece-name words used in the payoff sentences below. */
const PIECE_NAME_KEYS: Record<string, string> = {
  p: "piecePawn",
  n: "pieceKnight",
  b: "pieceBishop",
  r: "pieceRook",
  q: "pieceQueen",
  k: "pieceKing",
};

function pieceName(piece: string, t: Translator): string {
  const key = PIECE_NAME_KEYS[piece];
  return key ? t(key) : t("pieceGeneric");
}

export function formatSignedSeconds(amount: number): string {
  const rounded = Math.round(amount);
  return `${rounded > 0 ? "+" : ""}${rounded}s`;
}

/** Nc3 can take the knight on d5. Shared so Steal, Burn, and Bounty price the same trade. */
const KNIGHT_TRADE_FEN =
  "rnbqkb1r/ppp1pppp/8/3n4/8/2N5/PPPP1PPP/R1BQKBNR w KQkq - 0 4";

export const TUTORIAL_STEPS: TutorialStep[] = [
  {
    id: "win-without-mate",
    titleKey: "stepWinWithoutMateTitle",
    bodyKey: "stepWinWithoutMateBody",
    preset: "steal1",
    fen: null,
    whiteSeconds: 180,
    blackSeconds: 7,
  },
  {
    id: "moving-costs-time",
    titleKey: "stepMovingCostsTimeTitle",
    bodyKey: "stepMovingCostsTimeBody",
    preset: "steal1",
    fen: "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5Q2/PPPP1PPP/RNB1KBNR w KQkq - 2 3",
    whiteSeconds: fullClock("steal1"),
    blackSeconds: fullClock("steal1"),
    disallowCaptures: true,
    generatePayoff: ({ piece, moverDelta }, t) => {
      const p = pieceName(piece, t);
      const suffix =
        piece === "p"
          ? t("movingCostsTimePawnAside")
          : piece === "q"
            ? t("movingCostsTimeQueenAside")
            : "";
      return t("movingCostsTimePayoff", {
        piece: p,
        cost: formatSignedSeconds(moverDelta),
        suffix,
      });
    },
  },
  {
    id: "captures-steal",
    titleKey: "stepCapturesStealTitle",
    bodyKey: "stepCapturesStealBody",
    preset: "steal1",
    fen: KNIGHT_TRADE_FEN,
    whiteSeconds: fullClock("steal1"),
    blackSeconds: fullClock("steal1"),
    generatePayoff: ({ piece, captured, moverDelta, opponentDelta }, t) => {
      if (captured) {
        return t("capturesStealCapturedPayoff", {
          piece: pieceName(captured, t),
          gain: formatSignedSeconds(moverDelta),
          loss: formatSignedSeconds(opponentDelta),
        });
      }
      return t("capturesStealMovePayoff", {
        piece: pieceName(piece, t),
        cost: formatSignedSeconds(moverDelta),
      });
    },
  },
  {
    id: "captures-burn",
    titleKey: "stepCapturesBurnTitle",
    bodyKey: "stepCapturesBurnBody",
    preset: "burn1",
    fen: KNIGHT_TRADE_FEN,
    whiteSeconds: fullClock("burn1"),
    blackSeconds: fullClock("burn1"),
    generatePayoff: ({ piece, captured, moverDelta, opponentDelta }, t) => {
      if (captured) {
        return t("capturesBurnCapturedPayoff", {
          piece: pieceName(captured, t),
          net: formatSignedSeconds(moverDelta),
          loss: formatSignedSeconds(opponentDelta),
        });
      }
      return t("capturesBurnMovePayoff", {
        piece: pieceName(piece, t),
        cost: formatSignedSeconds(moverDelta),
      });
    },
  },
  {
    id: "captures-bounty",
    titleKey: "stepCapturesBountyTitle",
    bodyKey: "stepCapturesBountyBody",
    preset: "bounty1",
    fen: KNIGHT_TRADE_FEN,
    whiteSeconds: fullClock("bounty1"),
    blackSeconds: fullClock("bounty1"),
    generatePayoff: ({ piece, captured, moverDelta }, t) => {
      if (captured) {
        return t("capturesBountyCapturedPayoff", {
          piece: pieceName(captured, t),
          gain: formatSignedSeconds(moverDelta),
        });
      }
      return t("capturesBountyMovePayoff", {
        piece: pieceName(piece, t),
        cost: formatSignedSeconds(moverDelta),
      });
    },
  },
  {
    // P-2-2, Rule 3. Ra1-a8+ is the only checking move in this position (verified with chess.js:
    // check true, checkmate false, Black's only reply is Kg7), so `scriptedUci` makes it the only
    // legal move offered rather than leaving the check to be found among the king's other options.
    // The chip amount is not hardcoded here: `app/learn/page.tsx`'s handleMove calls
    // `checkChipDeltas` off `POOLS[preset].config` for any move that turns out to be a check
    // (see that file, "isCheck"). No step before this one ever played a checking move, so
    // that code path existed but never ran; this step is what exercises it.
    id: "check-chip",
    titleKey: "stepCheckTitle",
    bodyKey: "stepCheckBody",
    preset: "steal1",
    fen: "6k1/5p1p/8/8/8/8/5PPP/R5K1 w - - 0 1",
    whiteSeconds: fullClock("steal1"),
    blackSeconds: fullClock("steal1"),
    scriptedUci: ["a1a8"],
    generatePayoff: ({ moverDelta, opponentDelta }, t) =>
      t("stepCheckPayoff", {
        gain: formatSignedSeconds(moverDelta),
        loss: formatSignedSeconds(opponentDelta),
      }),
  },
  {
    // P-2-2, Rule 4. Nb1-a3, Nb8-a6, Na3-b1, Na6-b8: both knights out and back, nothing else moves,
    // so the fourth ply reconstructs the exact starting position (board + side to move + castling
    // rights + en passant target, verified with chess.js: identical to the opening FEN modulo the
    // halfmove/fullmove counters the engine's own PositionKey ignores too). That fourth ply is
    // therefore the position's second occurrence, which is exactly what
    // `config.RepeatedPositionNullifiesTime` (Rule 4) cancels: "once seen, always seen."
    id: "repetition-nullifies",
    titleKey: "stepRepetitionTitle",
    bodyKey: "stepRepetitionBody",
    preset: "steal1",
    fen: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    whiteSeconds: fullClock("steal1"),
    blackSeconds: fullClock("steal1"),
    scriptedUci: ["b1a3", "b8a6", "a3b1", "a6b8"],
    nullifiesLastMove: true,
    generatePayoff: (
      { piece, moverDelta, nullified, wouldBeMoverDelta, plyNumber, plyTotal },
      t,
    ) => {
      if (nullified) {
        return t("stepRepetitionNullifiedPayoff", {
          wouldBe: formatSignedSeconds(wouldBeMoverDelta ?? 0),
          actual: formatSignedSeconds(moverDelta),
        });
      }
      return t("stepRepetitionShufflePayoff", {
        piece: pieceName(piece, t),
        step: plyNumber ?? 1,
        total: plyTotal ?? 4,
        cost: formatSignedSeconds(moverDelta),
      });
    },
  },
  {
    id: "done",
    titleKey: "stepDoneTitle",
    bodyKey: "stepDoneBody",
    preset: "steal1",
    fen: null,
    whiteSeconds: 0,
    blackSeconds: 0,
  },
];
