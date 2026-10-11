import type { ClockHistoryPoint } from "@/components/ClockGraph";
import type { MoveListEntry } from "@/components/MoveList";
import { captureBaseSeconds } from "./costPreview";
import { capturesAlongLine, type PieceChar } from "./fen";
import type { GameSnapshot } from "./types";

/**
 * Everything the play page's sidebar holds that is a function of the plies played so far, rather
 * than of the position they produced.
 */
export interface GameHistory {
  moves: MoveListEntry[];
  clockHistory: ClockHistoryPoint[];
  /** Pieces White has taken off Black, in capture order. */
  capturedByWhite: PieceChar[];
  capturedByBlack: PieceChar[];
  /** Seconds each side has won from captures, the running total of their `CaptureGain` events. */
  captureSecondsWhite: number;
  captureSecondsBlack: number;
}

/**
 * Rebuilds the play page's move list, clock graph and captured-material panel from a `JoinGame`
 * snapshot.
 *
 * This is the client half of C-1-2. `JoinGame` used to return state and legal moves only, so a
 * rejoin had nothing to rebuild from and the page could only guard against making things worse: it
 * kept whatever it had, which silently omitted every ply that landed while the transport was down
 * and left every later `moveNumber` off by that count. The snapshot now carries the authoritative
 * move log, so this is derived purely from it and is therefore idempotent: calling it on the first
 * join and on every reconnect afterwards gives the same answer for the same game state, which is
 * what lets the page rebuild unconditionally instead of appending.
 *
 * Two things the log does not carry are reconstructed rather than dropped:
 *
 * - **Which piece each ply took.** `capturesAlongLine` replays the moves for occupancy only; the
 *   server stays the legality authority.
 * - **The seconds a capture won.** The log carries each ply's net clock delta, which is a capture
 *   gain, a movement toll and a check chip added together and not separable after the fact, so the
 *   gain is re-priced from the game's own `economy` through `captureBaseSeconds` - the same
 *   expression `computeCostPreview` prices the board's move hints with.
 */
export function historyFromSnapshot(snapshot: GameSnapshot): GameHistory {
  const log = snapshot.moves ?? [];
  const economy = snapshot.state.economy;
  const startSeconds = economy?.startTimeSeconds ?? snapshot.state.whiteTime;

  const victims = capturesAlongLine(log.map((m) => m.uci));
  const capturedByWhite: PieceChar[] = [];
  const capturedByBlack: PieceChar[] = [];
  let captureSecondsWhite = 0;
  let captureSecondsBlack = 0;

  log.forEach((move, index) => {
    const victim = victims[index];
    if (!victim) return;
    if (move.mover === "White") capturedByWhite.push(victim);
    else capturedByBlack.push(victim);

    // Mirrors the server: no CaptureGain event is emitted at all when the captor does not gain,
    // so there is nothing for the panel's "+Ns" to total.
    if (!economy?.captureCaptorGainEnabled) return;
    const gain = captureBaseSeconds(economy, victim);
    if (move.mover === "White") captureSecondsWhite += gain;
    else captureSecondsBlack += gain;
  });

  return {
    moves: log.map((m) => ({
      moveNumber: m.ply,
      side: m.mover,
      uci: m.uci,
      whiteDelta: m.whiteDelta,
      blackDelta: m.blackDelta,
    })),
    // The ply-0 point is the start of the graph, so it takes the configured starting clock, never
    // `state.whiteTime`: on a rejoin at ply 30 that is the *current* clock, and the graph would
    // open at whatever the player has left rather than at what they began with.
    clockHistory: [
      { moveNumber: 0, whiteTime: startSeconds, blackTime: startSeconds },
      ...log.map((m, i) => ({
        moveNumber: i + 1,
        whiteTime: m.whiteTimeAfter,
        blackTime: m.blackTimeAfter,
      })),
    ],
    capturedByWhite,
    capturedByBlack,
    captureSecondsWhite,
    captureSecondsBlack,
  };
}
