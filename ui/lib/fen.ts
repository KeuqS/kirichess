// Tiny FEN parser, just enough to support the cost-preview mirror and board helpers.
// The backend is the move-legality authority; this never validates legality.

export type PieceChar = "p" | "n" | "b" | "r" | "q" | "k";
export type Color = "w" | "b";

export interface Piece {
  type: PieceChar;
  color: Color;
}

export type Square = string; // algebraic, e.g. "e4"

/** File index 0-7 for 'a'-'h'. */
export function fileIndex(square: Square): number {
  return square.charCodeAt(0) - "a".charCodeAt(0);
}

/** Rank index 0-7 for '1'-'8'. */
export function rankIndex(square: Square): number {
  return square.charCodeAt(1) - "1".charCodeAt(0);
}

export function chebyshev(from: Square, to: Square): number {
  const df = Math.abs(fileIndex(to) - fileIndex(from));
  const dr = Math.abs(rankIndex(to) - rankIndex(from));
  return Math.max(df, dr);
}

/** Parses the placement field of a FEN into a square -> Piece|null map. */
export function parseBoard(fen: string): Record<Square, Piece | null> {
  const placement = fen.trim().split(" ")[0];
  const rows = placement.split("/");
  const board: Record<Square, Piece | null> = {};

  // FEN ranks go from 8 (top) down to 1 (bottom).
  for (let rowIdx = 0; rowIdx < rows.length; rowIdx++) {
    const rank = 8 - rowIdx;
    let file = 0;
    for (const ch of rows[rowIdx]) {
      if (/[1-8]/.test(ch)) {
        const empties = parseInt(ch, 10);
        for (let i = 0; i < empties; i++) {
          const square = `${String.fromCharCode("a".charCodeAt(0) + file)}${rank}`;
          board[square] = null;
          file++;
        }
      } else {
        const color: Color = ch === ch.toUpperCase() ? "w" : "b";
        const type = ch.toLowerCase() as PieceChar;
        const square = `${String.fromCharCode("a".charCodeAt(0) + file)}${rank}`;
        board[square] = { type, color };
        file++;
      }
    }
  }

  return board;
}

export function pieceAt(fen: string, square: Square): Piece | null {
  return parseBoard(fen)[square] ?? null;
}

export function sideToMoveFromFen(fen: string): Color {
  const parts = fen.trim().split(" ");
  return (parts[1] as Color) ?? "w";
}

export function isCapture(fen: string, uci: string): boolean {
  const from = uci.slice(0, 2);
  const to = uci.slice(2, 4);
  const targetPiece = pieceAt(fen, to);
  if (targetPiece) return true; // Normal capture

  const fromPiece = pieceAt(fen, from);
  if (fromPiece?.type === "p") {
    // En passant: Pawn moves diagonally to an empty square
    if (from[0] !== to[0]) return true;
  }
  return false;
}

/** The type of piece removed from the board by this move, or null if it isn't a capture. */
export function capturedPieceType(fen: string, uci: string): PieceChar | null {
  const from = uci.slice(0, 2);
  const to = uci.slice(2, 4);
  const targetPiece = pieceAt(fen, to);
  if (targetPiece) return targetPiece.type;

  const fromPiece = pieceAt(fen, from);
  if (fromPiece?.type === "p" && from[0] !== to[0]) return "p"; // en passant

  return null;
}

/**
 * The standard chess start position. Every live KiriChess game begins here: `GameSession` builds its
 * position with `KiriChessEngine.NewGame`, and `GameSession.Restore` falls back to
 * `KiriChessEngine.StartFen` for any row without an explicit `StartFen`.
 */
export const START_FEN =
  "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

/**
 * Replays a list of UCI plies and reports the piece each one captured, or null for a quiet move.
 *
 * This exists because the wire move log (`SpectatorMoveDto`) carries the move but not its victim,
 * and the captured-material panel has to be rebuilt from that log on every join (C-1-2). Occupancy
 * is all it tracks, and legality is never checked: the server accepted these plies already, and it
 * stays the authority, exactly as the note at the top of this file says.
 *
 * Castling, promotion and en passant are handled for one reason only, that a *later* ply's victim
 * resolves against the right piece. A rook left standing on h1 after O-O would make a subsequent
 * Rxh1 report a rook that is not there.
 */
export function capturesAlongLine(
  ucis: readonly string[],
  startFen: string = START_FEN,
): (PieceChar | null)[] {
  const board = parseBoard(startFen);
  const captured: (PieceChar | null)[] = [];

  for (const uci of ucis) {
    const from = uci.slice(0, 2);
    const to = uci.slice(2, 4);
    const promotion =
      uci.length > 4 ? (uci[4].toLowerCase() as PieceChar) : null;
    const mover = board[from] ?? null;
    const occupant = board[to] ?? null;

    let victim: PieceChar | null = occupant?.type ?? null;
    if (!occupant && mover?.type === "p" && from[0] !== to[0]) {
      // En passant: the pawn taken stands on the target file, on the mover's own rank.
      const passedSquare = `${to[0]}${from[1]}`;
      victim = board[passedSquare]?.type ?? "p";
      board[passedSquare] = null;
    }

    board[from] = null;
    board[to] = mover
      ? { type: promotion ?? mover.type, color: mover.color }
      : null;

    if (
      mover?.type === "k" &&
      Math.abs(fileIndex(to) - fileIndex(from)) === 2
    ) {
      const rank = from[1];
      const kingside = fileIndex(to) === 6;
      const rookFrom = `${kingside ? "h" : "a"}${rank}`;
      const rookTo = `${kingside ? "f" : "d"}${rank}`;
      board[rookTo] = board[rookFrom] ?? null;
      board[rookFrom] = null;
    }

    captured.push(victim);
  }

  return captured;
}

export function isCastle(fen: string, uci: string): boolean {
  const from = uci.slice(0, 2);
  const to = uci.slice(2, 4);
  const piece = pieceAt(fen, from);
  if (piece?.type === "k") {
    if (Math.abs(from.charCodeAt(0) - to.charCodeAt(0)) === 2) {
      return true;
    }
  }
  return false;
}
