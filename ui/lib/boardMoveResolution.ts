import { pieceAt, type PieceChar } from "./fen";

/**
 * Queen, rook, bishop, knight: piece value, strongest nearest the promotion square.
 * The spoken prompt in `a11y.ts` lists keys in a different order (Q, N, B, R); that order is
 * the sentence, this one is the buttons.
 */
export const PROMOTION_PICKER_ORDER = ["q", "r", "b", "n"] as const;
export type PromotionPickerPiece = (typeof PROMOTION_PICKER_ORDER)[number];

/**
 * Legal promotion pieces for a click from `from` to `to`, in picker order.
 * Empty when the click is not a promotion. A length of one means the move is forced
 * (queen would be illegal, or it is the only piece the position allows) and should play
 * immediately. Longer than one is the click-path picker (C-11-6).
 */
export function promotionChoices(
  legalUci: string[],
  from: string,
  to: string,
): PromotionPickerPiece[] {
  const prefix = `${from}${to}`;
  const available = new Set<string>();
  for (const uci of legalUci) {
    if (uci.length === 5 && uci.startsWith(prefix)) {
      available.add(uci[4].toLowerCase());
    }
  }
  return PROMOTION_PICKER_ORDER.filter((piece) => available.has(piece));
}

export interface PromotionPickerLayout {
  /** Algebraic squares the buttons cover, strongest piece first. The first is `to`. */
  squares: string[];
  /** The destination is on the top edge of the screen, so the stack grows downward. */
  growsDown: boolean;
}

/**
 * Where the click-path picker sits. The queen button covers the destination square and the
 * rest step one rank toward the middle of the screen, so a white pawn promoting on the 8th
 * grows down the file and a black pawn promoting on the 1st grows up it. `orientation` is the
 * board's, which flips which edge is the top.
 */
export function promotionPickerLayout(
  to: string,
  orientation: "white" | "black",
  count: number,
): PromotionPickerLayout {
  const file = to[0];
  const rank = Number(to[1]);
  const growsDown = orientation === "white" ? rank === 8 : rank === 1;
  const step = orientation === "white" ? (growsDown ? -1 : 1) : growsDown ? 1 : -1;
  const squares: string[] = [];
  for (let i = 0; i < count; i++) {
    squares.push(`${file}${rank + step * i}`);
  }
  return { squares, growsDown };
}

/**
 * Resolves the UCI move a `Board` interaction is requesting, or `null` if it isn't legal.
 *
 * `Board.tsx` has two ways a player identifies a move, and this covers both:
 *
 * - **Click-to-move** (`droppedPiece` omitted): `from` is the previously selected square, `to`
 *   is the square just clicked. A promotion with several legal pieces returns `null` here -
 *   `promotionChoices` is what the click path uses to open the picker instead of inventing a
 *   queen. A promotion with exactly one legal piece plays that piece. Clicking a same-side rook
 *   while its king is the selected square is translated to castling notation (`e1g1` / `e1c1`)
 *   even though the rook's own square is never itself the castling move's legal target - this is
 *   what lets a player castle by clicking king then rook, the way Lichess/Chess.com allow.
 * - **Drag-and-drop** (`droppedPiece` given, e.g. `"wQ"`): `react-chessboard` has already run its
 *   own promotion flow and handed back the piece glyph the player ended up with, so the
 *   promotion piece comes from that glyph instead of defaulting to queen. There is no
 *   castle-by-drop-on-rook affordance; dropping a king on its rook is just an (illegal) attempt to
 *   move onto that square.
 *
 * Pure and deterministic: no state, no I/O, safe to unit-test directly. `Board.tsx`'s
 * `handleSquareClick` and `handlePieceDrop` are both thin wrappers around this function so the
 * logic under test is the logic that actually runs, not a parallel copy that can drift from it.
 */
export function resolveBoardMove(
  fen: string,
  legalUci: string[],
  from: string,
  to: string,
  droppedPiece?: string,
): string | null {
  const fromPiece = pieceAt(fen, from);

  if (droppedPiece === undefined) {
    const toPiece = pieceAt(fen, to);
    if (fromPiece?.type === "k" && toPiece?.type === "r") {
      const rank = from[1];
      const isKingside = to[0] === "h";
      const isQueenside = to[0] === "a";
      if (isKingside || isQueenside) {
        const destFile = isKingside ? "g" : "c";
        const castleUci = `${from}${destFile}${rank}`;
        if (legalUci.includes(castleUci)) return castleUci;
      }
    }

    const choices = promotionChoices(legalUci, from, to);
    if (choices.length > 1) return null;
    const uci =
      choices.length === 1 ? `${from}${to}${choices[0]}` : `${from}${to}`;
    return legalUci.includes(uci) ? uci : null;
  }

  const droppedType = droppedPiece[1].toLowerCase() as PieceChar;
  const isPromotion = fromPiece?.type === "p" && droppedType !== "p";
  const uci = isPromotion ? `${from}${to}${droppedType}` : `${from}${to}`;
  return legalUci.includes(uci) ? uci : null;
}
