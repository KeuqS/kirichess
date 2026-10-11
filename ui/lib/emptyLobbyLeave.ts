/**
 * P-3-3. Module flags for the empty-lobby bounce. In-memory only: no visitor id, no session id.
 * Once a visitor queues or starts a game this tab, a later close is not an empty-lobby leave.
 */

let queued = false;
let startedGame = false;

export function markQueued(): void {
  queued = true;
}

export function markGameStarted(): void {
  startedGame = true;
}

export function shouldRecordEmptyLobbyLeave(args: {
  internalNav: boolean;
  seekCount: number;
  ready: boolean;
}): boolean {
  return (
    args.ready &&
    !args.internalNav &&
    !queued &&
    !startedGame &&
    args.seekCount === 0
  );
}

/** Clears the module flags so vitest cases do not leak into each other. */
export function resetEmptyLobbyLeave(): void {
  queued = false;
  startedGame = false;
}
