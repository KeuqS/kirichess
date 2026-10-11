// Pure accessibility description helpers (Phase 9f.2). No React here: `vitest.config.mts` only
// runs `lib/**/*.test.ts` under a `node` environment (no jsdom, no Testing Library), so this file
// is the only place in the accessibility feature that can carry test coverage. Every fact worth
// getting wrong lives here; `components/AccessibleBoard.tsx` and `components/LiveAnnouncer.tsx`
// call into this and stay thin.
//
// P-8-4: spoken strings live in the `a11y` catalogue. Callers pass a next-intl translator, the
// same pattern `lib/tutorial.ts` uses.

import {
  capturedPieceType,
  isCapture,
  isCastle,
  pieceAt,
  type PieceChar,
  type Square,
} from "./fen";
import type { Translator } from "./tutorial";
import type { ClockEventDto, Side } from "./types";

export type { Translator };

export const FILES = ["a", "b", "c", "d", "e", "f", "g", "h"] as const;
/** Rank 8 first: the grid is rendered and read top-down, the way a board is drawn. */
export const RANKS = ["8", "7", "6", "5", "4", "3", "2", "1"] as const;

function capitalize(text: string): string {
  return text.length === 0 ? text : text[0].toUpperCase() + text.slice(1);
}

function pieceName(t: Translator, type: PieceChar): string {
  return t(`piece.${type}`);
}

function secondsPhrase(t: Translator, seconds: number): string {
  return t("seconds", { count: seconds });
}

/** "e5, black pawn" or "e5, empty". Colour is named here because this describes the board. */
export function describeSquare(
  fen: string,
  square: Square,
  t: Translator,
): string {
  const piece = pieceAt(fen, square);
  if (!piece) return t("squareEmpty", { square });
  const colorName = t(piece.color === "w" ? "colorWhite" : "colorBlack");
  return t("squareOccupied", {
    square,
    color: colorName,
    piece: pieceName(t, piece.type),
  });
}

/**
 * The accessible name of a square while `from` is selected. Appends the move's own price, which is
 * the point of the whole feature (see design decision 4). `cost` is `costFor(from, to)`: signed
 * seconds, negative meaning the mover pays. `cost === 0` and `cost === undefined` both drop the
 * price clause: an explicit "costs 0 seconds" reads as noise, and undefined means no state to price
 * from yet (mirrors `describeMove`'s "never say plus 0").
 */
export function describeTarget(
  fen: string,
  from: Square,
  to: Square,
  cost: number | undefined,
  t: Translator,
): string {
  const parts = [describeSquare(fen, to, t)];
  if (isCapture(fen, `${from}${to}`)) parts.push(t("capture"));
  if (cost !== undefined && cost !== 0) {
    const phrase = secondsPhrase(t, Math.abs(cost));
    parts.push(cost < 0 ? t("costsYou", { time: phrase }) : t("gainsYou", { time: phrase }));
  }
  return parts.join(", ");
}

/**
 * Spoken confirmation when a piece is selected (P-5-4). Deselect already said "Selection cleared.";
 * select was silent, so NVDA could miss `aria-selected` flipping on the already-focused cell.
 * Target count is unique destination squares, not UCI strings: a promotion that is legal as queen
 * and knight is still one target.
 */
export function describeSelection(
  square: Square,
  legalTargetCount: number,
  t: Translator,
): string {
  if (legalTargetCount <= 0) return t("selectionNone", { square });
  if (legalTargetCount === 1) return t("selectionOne", { square });
  return t("selectionMany", { square, count: legalTargetCount });
}

/** Pieces a pawn can promote to, in the order the prompt lists their keys. */
export const PROMOTION_PIECES = ["q", "n", "b", "r"] as const;
export type PromotionPiece = (typeof PROMOTION_PIECES)[number];

/**
 * Spoken prompt when the arrow-key path lands on a last-rank pawn move with more than one legal
 * promotion piece (P-5-6). Typed entry already accepted `e7e8n`; this is the same choice for
 * someone navigating the grid. Enter or Space still picks queen. The visual click path asks
 * with a piece picker instead of defaulting.
 */
export function describePromotionPrompt(
  to: Square,
  pieces: readonly PromotionPiece[],
  t: Translator,
): string {
  const ordered = PROMOTION_PIECES.filter((p) => pieces.includes(p));
  const options = ordered.map((p) =>
    t("promoteOption", { key: p.toUpperCase(), piece: pieceName(t, p) }),
  );
  if (options.length === 0) return t("promoteOn", { square: to });
  if (options.length === 1)
    return t("promotePressOne", { square: to, option: options[0] });
  const head = options.slice(0, -1).join(", ");
  return t("promotePressMany", {
    square: to,
    head,
    last: options[options.length - 1],
  });
}

/**
 * The lowercase "what moved" core of an utterance (capitalized by the caller, `describeMove`, which
 * also appends the price and the board consequence). Reuses `lib/fen.ts`'s move classification
 * rather than reimplementing chess logic: `isCastle`/`isCapture`/`capturedPieceType` already handle
 * this exact UCI shape and are the same functions the game page uses to pick a sound effect.
 */
function moveCore(fen: string, uci: string, t: Translator): string {
  const from = uci.slice(0, 2);
  const to = uci.slice(2, 4);

  if (isCastle(fen, uci)) {
    // The king's destination file is the same test Board.tsx uses to tell the two sides apart.
    return to[0] === "g" ? t("castlesKingside") : t("castlesQueenside");
  }

  if (uci.length > 4) {
    const promotedTo = pieceName(t, uci[4].toLowerCase() as PieceChar);
    return t("pawnPromotes", { square: to, piece: promotedTo });
  }

  const mover = pieceAt(fen, from);
  const moverName = mover ? pieceName(t, mover.type) : t("fallbackPiece");

  if (isCapture(fen, uci)) {
    const victim = capturedPieceType(fen, uci);
    const victimName = victim ? pieceName(t, victim) : t("fallbackPiece");
    return t("pieceTakes", { mover: moverName, square: to, victim: victimName });
  }

  return t("pieceTo", { piece: moverName, square: to });
}

/**
 * One utterance: move, price, consequence (design decision 2). `events` is the same
 * `ClockEventDto[]` the hub sends with every `MovePlayed`; `you` is the listener's side, so the
 * signed amounts can be resolved to "you" and "opponent". Canonical order is fixed: what moved,
 * what it cost you, what it gave them, and only then the board consequence (check) - see the design
 * section, decision 2. Never a separate "plus 0": a clause whose net is zero is dropped entirely,
 * and the whole economy clause is dropped when both nets are zero.
 *
 * `mover` is accepted (not used below) for call-site symmetry with the `MovePlayed` handler, which
 * always has both `you` and the side that just moved on hand; the canonical wording is deliberately
 * side-neutral ("Check.", not "you are in check"), so nothing here currently branches on it.
 */
export function describeMove(args: {
  priorFen: string;
  uci: string;
  events: ClockEventDto[];
  you: Side;
  mover: Side;
  check?: boolean;
  t: Translator;
}): string {
  const { priorFen, uci, events, you, check, t } = args;

  let netWhite = 0;
  let netBlack = 0;
  for (const ev of events) {
    if (ev.side === "White") netWhite += ev.amount;
    else netBlack += ev.amount;
  }
  const netYou = you === "White" ? netWhite : netBlack;
  const netOpponent = you === "White" ? netBlack : netWhite;

  const economyParts: string[] = [];
  if (netYou !== 0) {
    economyParts.push(
      netYou > 0
        ? t("youPlus", { amount: Math.abs(netYou) })
        : t("youMinus", { amount: Math.abs(netYou) }),
    );
  }
  if (netOpponent !== 0) {
    economyParts.push(
      netOpponent > 0
        ? t("opponentPlus", { amount: Math.abs(netOpponent) })
        : t("opponentMinus", { amount: Math.abs(netOpponent) }),
    );
  }

  const clauses = [moveCore(priorFen, uci, t)];
  if (economyParts.length > 0) clauses.push(economyParts.join(", "));
  if (check) clauses.push(t("check"));

  return `${clauses.map(capitalize).join(". ")}.`;
}

/** Spoken time: 92 -> "1 minute 32 seconds", 8 -> "8 seconds". Never "1:32", which reads as a date. */
export function spokenTime(seconds: number, t: Translator): string {
  const total = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(total / 60);
  const secs = total % 60;
  if (minutes === 0) return secondsPhrase(t, secs);
  const minutePhrase = t("minutes", { count: minutes });
  if (secs === 0) return minutePhrase;
  return t("minutesAndSeconds", {
    minutes: minutePhrase,
    seconds: secondsPhrase(t, secs),
  });
}

/**
 * "You 1 minute 32 seconds, opponent 2 minutes 5 seconds. You are behind by 33 seconds." Built from
 * `spokenTime` throughout, never a raw "1:32": that reads as a date to a screen reader (see
 * `spokenTime`'s own comment), and the on-demand clock readout is exactly the surface where a naive
 * implementation is tempted to reach for the clock's own `M:SS` display string instead.
 */
export function describeClocks(
  yourSeconds: number,
  opponentSeconds: number,
  t: Translator,
): string {
  const gap = yourSeconds - opponentSeconds;
  const you = spokenTime(yourSeconds, t);
  const opponent = spokenTime(opponentSeconds, t);
  if (gap === 0) return t("clocksLevel", { you, opponent });
  if (gap > 0)
    return t("clocksAhead", { you, opponent, gap: spokenTime(gap, t) });
  return t("clocksBehind", { you, opponent, gap: spokenTime(-gap, t) });
}

export const URGENCY_THRESHOLDS = [60, 30, 15, 10, 5, 4, 3, 2, 1] as const;

/**
 * Which urgency threshold, if any, the clock crossed between two readings. Answered from the pair
 * rather than from the current value alone, which is what makes it robust and what makes the
 * caller's bookkeeping disappear:
 *
 * - **No missed announcements.** Matching a threshold against the current second only works if a
 *   reading lands inside every threshold second. The game page's ticker is a 100ms interval, and a
 *   backgrounded tab, a GC pause or a slow frame can stretch a gap past a full second, silently
 *   skipping a threshold. Under time pressure, with a screen reader, at 3 seconds left, that is
 *   precisely the announcement that must not be the one that goes missing.
 * - **No re-arming.** This variant hands time back, so a clock can cross 30 seconds several times
 *   in one game (design decision 3). A crossing is a fact about a pair of readings, so rising back
 *   above a threshold and falling through it again is simply a second crossing. There is no
 *   "already announced" flag to reset, and therefore no way to leave one stuck.
 *
 * Returns the **most urgent** threshold crossed when a single gap swallows several (falling from 12
 * to 4 announces "5 seconds", not "10 seconds"): the lower number is the one that is true now.
 * Returns null when nothing was crossed, including when the clock is rising.
 */
export function crossedThreshold(
  previousSeconds: number,
  currentSeconds: number,
): number | null {
  const crossed = URGENCY_THRESHOLDS.filter(
    (th) => previousSeconds > th && currentSeconds <= th,
  );
  return crossed.length === 0 ? null : Math.min(...crossed);
}
