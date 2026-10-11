import { isPresetKey, PRESETS } from "./presets";
import type { LiveGameDto } from "./types";

/**
 * Static boards for the /watch grid. Shown only when SAMPLE is flipped on in
 * `ui/app/watch/page.tsx` - a local layout check, not a fallback for an empty lobby.
 * They are not spectatable, so the card is rendered without a link.
 */
const STARTED_AT = "2026-08-22T18:00:00.000Z";

function demo(partial: {
  n: number;
  whiteName: string;
  blackName: string;
  pool: string | null;
  fen: string;
  sideToMove: LiveGameDto["sideToMove"];
  moveCount: number;
  whiteTime: number;
  blackTime: number;
  spectators: number;
  rated?: boolean;
  /** Defaults to "None": most demo boards carry no badge, a couple do to prove the card renders one. */
  whitePatronTier?: LiveGameDto["whitePatronTier"];
  blackPatronTier?: LiveGameDto["blackPatronTier"];
}): LiveGameDto {
  return {
    gameId: `00000000-0000-4000-a000-${partial.n.toString().padStart(12, "0")}`,
    whiteName: partial.whiteName,
    blackName: partial.blackName,
    whitePatronTier: partial.whitePatronTier ?? "None",
    blackPatronTier: partial.blackPatronTier ?? "None",
    pool: partial.pool,
    rated: partial.rated ?? partial.pool !== null,
    whiteTime: partial.whiteTime,
    blackTime: partial.blackTime,
    sideToMove: partial.sideToMove,
    moveCount: partial.moveCount,
    spectators: partial.spectators,
    startedAt: STARTED_AT,
    fen: partial.fen,
  };
}

/** Remaining seconds must sit inside the pool's start clock so a card cannot show more time than it started with. */
function remaining(pool: string | null, seconds: number): number {
  const start =
    pool && isPresetKey(pool) ? PRESETS[pool].startTimeSeconds : 300;
  return Math.min(seconds, start);
}

export const WATCH_DEMO_GAMES: LiveGameDto[] = [
  demo({
    n: 1,
    whiteName: "Mira",
    blackName: "Ren",
    pool: "steal1",
    fen: "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3",
    sideToMove: "Black",
    moveCount: 5,
    whiteTime: remaining("steal1", 248),
    blackTime: remaining("steal1", 261),
    spectators: 14,
    whitePatronTier: "Queen",
  }),
  demo({
    n: 2,
    whiteName: "Oka",
    blackName: "Lev",
    pool: "bounty1",
    fen: "rnbqkb1r/1p2pppp/p2p1n2/8/3NP3/2N5/PPP2PPP/R1BQKB1R b KQkq - 0 6",
    sideToMove: "Black",
    moveCount: 12,
    whiteTime: remaining("bounty1", 41),
    blackTime: remaining("bounty1", 37),
    spectators: 6,
  }),
  demo({
    n: 3,
    whiteName: "Nia",
    blackName: "Soren",
    pool: "burn1",
    fen: "rnbqk2r/pp2nppp/4p3/2ppP3/3P4/P1P5/2P2PPP/R1BQKBNR b KQkq - 0 6",
    sideToMove: "Black",
    moveCount: 11,
    whiteTime: remaining("burn1", 176),
    blackTime: remaining("burn1", 188),
    spectators: 3,
  }),
  demo({
    n: 4,
    whiteName: "Tess",
    blackName: "Kai",
    pool: "steal2",
    fen: "rnbq1rk1/ppp1ppbp/3p1np1/8/2PPP3/2N2N2/PP3PPP/R1BQKB1R b KQ - 0 6",
    sideToMove: "Black",
    moveCount: 12,
    whiteTime: remaining("steal2", 211),
    blackTime: remaining("steal2", 194),
    spectators: 22,
  }),
  demo({
    n: 5,
    whiteName: "Ilya",
    blackName: "Rowan",
    pool: "steal1",
    fen: "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
    sideToMove: "White",
    moveCount: 7,
    whiteTime: remaining("steal1", 302),
    blackTime: remaining("steal1", 318),
    spectators: 9,
  }),
  demo({
    n: 6,
    whiteName: "Anouk",
    blackName: "Piet",
    pool: "burn2",
    fen: "rnbqkb1r/ppp1pppp/8/3n4/8/2N5/PPPP1PPP/R1BQKBNR w KQkq - 0 4",
    sideToMove: "White",
    moveCount: 7,
    whiteTime: remaining("burn2", 841),
    blackTime: remaining("burn2", 870),
    spectators: 2,
  }),
  demo({
    n: 7,
    whiteName: "Noor",
    blackName: "Eli",
    pool: "steal3",
    fen: "r2q1rk1/ppp1bppp/2n1bn2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 9",
    sideToMove: "White",
    moveCount: 17,
    whiteTime: remaining("steal3", 612),
    blackTime: remaining("steal3", 588),
    spectators: 31,
  }),
  demo({
    n: 8,
    whiteName: "Saskia",
    blackName: "Jon",
    pool: null,
    fen: "r2q1rk1/1ppb1ppp/p1np1n2/4p3/B3P3/2PP1N2/PP3PPP/RNBQR1K1 b - - 0 9",
    sideToMove: "Black",
    moveCount: 18,
    whiteTime: remaining(null, 204),
    blackTime: remaining(null, 191),
    spectators: 1,
    rated: false,
  }),
  demo({
    n: 9,
    whiteName: "Vera",
    blackName: "Otto",
    pool: "steal1",
    fen: "8/8/8/4k3/8/8/4Q3/4K2R w - - 0 1",
    sideToMove: "White",
    moveCount: 64,
    whiteTime: remaining("steal1", 48),
    blackTime: remaining("steal1", 12),
    spectators: 40,
    blackPatronTier: "Knight",
  }),
  demo({
    n: 10,
    whiteName: "Lina",
    blackName: "Bo",
    pool: "bounty1",
    fen: "8/5pk1/p6p/8/8/5P2/P5PP/4R1K1 w - - 0 40",
    sideToMove: "White",
    moveCount: 79,
    whiteTime: remaining("bounty1", 22),
    blackTime: remaining("bounty1", 0),
    spectators: 8,
  }),
  demo({
    n: 11,
    whiteName: "Rafi",
    blackName: "Mei",
    pool: "steal2",
    fen: "r1b2rk1/pp1n1ppp/2p1p3/3n4/2BP4/2N1PN2/PP3PPP/R2Q1RK1 w - - 0 12",
    sideToMove: "White",
    moveCount: 23,
    whiteTime: remaining("steal2", 155),
    blackTime: remaining("steal2", 142),
    spectators: 17,
  }),
  demo({
    n: 12,
    whiteName: "Ada",
    blackName: "Nils",
    pool: "burn1",
    fen: "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2",
    sideToMove: "White",
    moveCount: 2,
    whiteTime: remaining("burn1", 228),
    blackTime: remaining("burn1", 234),
    spectators: 0,
  }),
];
