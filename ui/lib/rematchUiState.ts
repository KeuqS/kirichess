export type RematchUiState = "idle" | "offered" | "incoming";

/**
 * Overlay state for a rematch offer already sitting on the finished game (P-3-1). The mail
 * recipient never saw RematchOffered, so JoinGame's rematchOfferedById is what chooses Accept
 * versus Offer. Missing either id means nobody has offered yet.
 */
export function rematchUiState(
  userId: string | null | undefined,
  offeredById: string | null | undefined,
): RematchUiState {
  if (!userId || !offeredById) return "idle";
  return offeredById === userId ? "offered" : "incoming";
}
