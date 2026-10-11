import { Chess } from "chess.js";
import { isCapture, isCastle } from "./fen";

/** The board-move cues live games, replays, and the tutorial play. */
export type MoveSound = "move" | "capture" | "castle" | "check";

/** Outcome cues the live game page plays on top of `MoveSound`. */
export type ChessSound = MoveSound | "start" | "win" | "loss" | "draw";

/** Public URLs for the set in `ui/public/sounds/`. Provenance: `SOUNDS.LICENSE`. */
export const SOUND_SRC: Record<ChessSound, string> = {
  move: "/sounds/move.mp3",
  capture: "/sounds/capture.mp3",
  castle: "/sounds/castle.mp3",
  check: "/sounds/check.mp3",
  start: "/sounds/start.mp3",
  win: "/sounds/win.mp3",
  loss: "/sounds/loss.mp3",
  draw: "/sounds/draw.mp3",
};

/**
 * Which move cue to play for this ply. Check wins over capture and castle, the same
 * way a live board announces the position the opponent is left in. Capture wins over
 * castle, otherwise a quiet move.
 */
export function soundForMove(fen: string, uci: string): MoveSound {
  if (leavesOpponentInCheck(fen, uci)) return "check";
  if (isCapture(fen, uci)) return "capture";
  if (isCastle(fen, uci)) return "castle";
  return "move";
}

/**
 * Which cue a replay scrub should play. One step forward plays the move just made.
 * One step back plays the move just undone. A jump plays only the move now on the
 * board, and returning to the start position plays nothing.
 */
export function soundForReplayStep(
  startFen: string,
  plies: readonly { fenAfter: string; uci: string }[],
  from: number,
  to: number,
): MoveSound | null {
  if (to === from) return null;
  const index = to < from && to === from - 1 ? from - 1 : to - 1;
  if (index < 0 || index >= plies.length) return null;
  const fenBefore = index === 0 ? startFen : plies[index - 1].fenAfter;
  return soundForMove(fenBefore, plies[index].uci);
}

function leavesOpponentInCheck(fen: string, uci: string): boolean {
  try {
    const chess = new Chess(fen);
    const from = uci.slice(0, 2);
    const to = uci.slice(2, 4);
    const promotion = uci.length > 4 ? uci[4] : undefined;
    const played = chess.move({ from, to, promotion });
    return played !== null && chess.inCheck();
  } catch {
    return false;
  }
}
