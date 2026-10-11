import { analyzeClockSwings } from "./clockAnalysis";
import type { ReplayResponse, Termination } from "./types";

// Duplicates ui/app/replay/[id]/page.tsx's RESULT_LABEL and GameOverOverlay's TERMINATION_LABEL
// rather than importing them: both live in "use client" files, and importing a client-directive
// module into generateMetadata/opengraph-image (server-only, Phase 9d.6) would pull its whole
// client bundle graph (react-chessboard, dynamic() Board import, etc.) into a route that never
// renders any of it. These two maps are the only part actually needed here.
// `ReplayResponse["result"]` carries the full GameResult enum, including "Ongoing", because the
// generated schema is not endpoint-specific; the route itself (ReplayEndpoints.cs) filters ongoing
// games out with `g.Result != GameResult.Ongoing` before this ever reaches the client, so this case
// is unreachable in practice but still required to keep the map total (Phase 9d.7 cutover).
const RESULT_LABEL: Record<ReplayResponse["result"], string> = {
  Ongoing: "Game in progress",
  WhiteWins: "White wins",
  BlackWins: "Black wins",
  Draw: "Draw",
  Aborted: "Game aborted",
};

const TERMINATION_LABEL: Record<Termination, string> = {
  None: "game over",
  Checkmate: "checkmate",
  Flagfall: "flag fall",
  Stalemate: "stalemate",
  Resignation: "resignation",
  DrawByRepetition: "repetition",
  DrawByInsufficientMaterial: "insufficient material",
  DrawByFiftyMove: "the fifty-move rule",
  Abandoned: "abandonment",
  DrawByAgreement: "agreement",
  ServerRestart: "a server restart",
  NoShow: "a no-show",
};

export interface ReplayCardText {
  title: string;
  description: string;
}

export function replayCardText(replay: ReplayResponse): ReplayCardText {
  const title = `${replay.whitePlayerName} vs ${replay.blackPlayerName}`;
  const result = RESULT_LABEL[replay.result];
  const termination =
    TERMINATION_LABEL[replay.termination] ?? replay.termination;
  const moveCount = replay.plies.length;
  const incomplete =
    replay.truncatedAtPly == null ? "" : " The stored record is incomplete.";
  const description = `${result} by ${termination}, ${moveCount} ${moveCount === 1 ? "ply" : "plies"}.${incomplete} Watch the replay on KiriChess.`;
  return { title, description };
}

export interface ReplayCardSwing {
  ply: number;
  san: string;
  magnitude: number;
  favoredName: string;
}

/**
 * The single biggest clock swing of the game, for the share card (Phase AD.9): 9e.3's own analysis,
 * asked for its top-1 rather than the replay page's top-3, and reduced to just what a 1200x630 image
 * needs to render. Null when nothing in the game cleared 9e.3's swing threshold - a quiet, even game
 * has no standout moment, and the card falls back to `replayCardText` alone rather than inventing one.
 */
export function replayCardSwing(
  replay: ReplayResponse,
): ReplayCardSwing | null {
  const [top] = analyzeClockSwings(
    replay.plies,
    replay.economy.startTimeSeconds,
    1,
  ).swings;
  if (!top) return null;

  return {
    ply: top.ply,
    san: top.san || top.uci,
    magnitude: top.magnitude,
    favoredName:
      top.favoredSide === "White"
        ? replay.whitePlayerName
        : replay.blackPlayerName,
  };
}
