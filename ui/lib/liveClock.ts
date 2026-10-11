import type { Side } from "./types";

/**
 * The whole-second clocks the server sends, plus the milliseconds already charged against the
 * side to move. Without the offset a countdown starts a fraction of a second (up to one) late,
 * and the flag can fall while the digits still show time.
 */
export interface LiveClockReading {
  whiteTime: number;
  blackTime: number;
  sideToMove: Side;
  activeClockOffsetMs: number;
}

/** A client-side countdown origin. `time` is `performance.now()` at the moment the reading arrived. */
export interface ClockSync {
  time: number;
  whiteTime: number;
  blackTime: number;
  sideToMove: Side;
}

/**
 * Starts a countdown from the server's reading. The offset comes off the side to move only; the
 * other clock is frozen. `now` is the caller's monotonic clock (`performance.now()`), the same
 * clock later passed to `displayClocks`.
 */
export function clockSyncFromState(state: LiveClockReading, now: number): ClockSync {
  const offsetSec = Math.max(0, state.activeClockOffsetMs) / 1000;
  const white =
    state.sideToMove === "White" ? state.whiteTime - offsetSec : state.whiteTime;
  const black =
    state.sideToMove === "Black" ? state.blackTime - offsetSec : state.blackTime;
  return {
    time: now,
    whiteTime: Math.max(0, white),
    blackTime: Math.max(0, black),
    sideToMove: state.sideToMove,
  };
}

/**
 * Remaining clocks at monotonic `now`. Elapsed is never negative: a tick older than the sync
 * (the render that applied the packet) shows the synced value rather than adding time.
 */
export function displayClocks(
  sync: ClockSync,
  now: number,
  ongoing: boolean,
): { white: number; black: number } {
  if (!ongoing) return { white: sync.whiteTime, black: sync.blackTime };
  const elapsed = Math.max(0, (now - sync.time) / 1000);
  return {
    white:
      sync.sideToMove === "White"
        ? Math.max(0, sync.whiteTime - elapsed)
        : sync.whiteTime,
    black:
      sync.sideToMove === "Black"
        ? Math.max(0, sync.blackTime - elapsed)
        : sync.blackTime,
  };
}
