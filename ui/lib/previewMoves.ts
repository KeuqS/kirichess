import { Chess } from "chess.js";

/**
 * Legal destinations for `color` on the current placement, even when the FEN's side to move is
 * the other colour. Used to paint destination dots on a piece the local player has highlighted
 * while the opponent is thinking. This is not a move list the client may submit: the server's
 * `legalUci` is still the only list a click is allowed to play, and only on that player's turn.
 *
 * En passant is cleared when the turn is flipped. That capture belongs to the side that was
 * just moved against, which is the opponent, not the piece being previewed.
 */
export function previewUci(fen: string, color: "w" | "b"): string[] {
  const parts = fen.trim().split(/\s+/);
  if (parts.length < 4 || (parts[1] !== "w" && parts[1] !== "b")) return [];
  const sameTurn = parts[1] === color;
  if (!sameTurn) {
    parts[1] = color;
    parts[3] = "-";
  }
  try {
    const chess = new Chess(parts.join(" "), { skipValidation: !sameTurn });
    return chess.moves({ verbose: true }).map((move) => {
      return move.from + move.to + (move.promotion ?? "");
    });
  } catch {
    return [];
  }
}
