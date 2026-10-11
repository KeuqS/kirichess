/**
 * The slice of the DOM `Worker` this module uses. Declared with method syntax so a real `Worker`
 * is assignable to it, and so a test can pass a fake without a `Worker` polyfill; `addEventListener`
 * rather than `onmessage` for the same reason (an `onmessage` property is checked contravariantly
 * and a real `Worker` would not fit).
 */
export interface BotWorkerLike {
  postMessage(message: string): void;
  terminate(): void;
  addEventListener(
    type: "message",
    listener: (event: { data: unknown }) => void,
  ): void;
}

/**
 * Thrown into a search that will never produce a move because a newer search replaced it, or
 * because the engine was disposed. Distinct from an engine failure on purpose: a caller must not
 * answer this by playing a random move, since another search already owns the turn.
 */
export class BotSearchAbandonedError extends Error {
  constructor(reason: string) {
    super(`Bot search abandoned: ${reason}`);
    this.name = "BotSearchAbandonedError";
  }
}

/** True for the one rejection {@link BotEngine.getMove} produces that is not an engine failure. */
export function isBotSearchAbandoned(error: unknown): boolean {
  return error instanceof BotSearchAbandonedError;
}

/** One Stockfish worker, owned by whoever created it. */
export interface BotEngine {
  getMove(fen: string, level: number, legalMoves: string[]): Promise<string>;
  /** Terminates the worker and abandons any search still in flight. Safe to call twice. */
  dispose(): void;
}

function defaultWorker(): BotWorkerLike {
  if (typeof window === "undefined") {
    // Should never happen since this is only called from an effect, but just in case
    throw new Error("Cannot run Stockfish on server");
  }
  // Next.js serves files from public/ at the site root. The name is the Stockfish 19
  // lite release: the worker fetches the sibling .wasm by replacing this filename's
  // .js suffix, and both are cached immutable, so a later swap has to change the name.
  return new Worker("/stockfish-19-lite-single.js");
}

/**
 * Creates an engine scoped to one game (C-1-7).
 *
 * This was a module-level worker singleton with a single `resolveBestMove` slot, and both halves of
 * that were wrong in ways nothing announced. The worker was never terminated, so it kept running
 * after the game ended and after navigation; and because the one resolver was simply overwritten by
 * whichever search started last, the *previous* search's `bestmove` - computed for a different
 * position, and after a route change for a different game entirely - resolved the newer promise. The
 * bot then played a move for a board that no longer existed, which reaches the player as an illegal
 * move rejected by the server, or worse as a legal but nonsensical one.
 *
 * Two things fix it, and they close different holes:
 *
 * - **A worker per engine, disposed by its owner.** Nothing survives the game it belongs to, so a
 *   reply can never cross from one game into another.
 * - **Searches counted, not overwritten.** UCI answers exactly one `bestmove` per `go`, in order, so
 *   the Nth reply belongs to the Nth search. A reply is delivered only to the search whose ordinal it
 *   matches; a superseded search is rejected with {@link BotSearchAbandonedError} rather than left
 *   pending, so its caller finds out instead of hanging.
 */
export function createBotEngine(
  createWorker: () => BotWorkerLike = defaultWorker,
): BotEngine {
  let worker: BotWorkerLike | null = null;
  let disposed = false;

  // `go` commands sent, and `bestmove` lines seen. The invariant that makes this work is UCI's:
  // one bestmove per go, in order, so reply number N answers search number N.
  let searchesStarted = 0;
  let repliesSeen = 0;
  let pending: {
    id: number;
    resolve: (move: string) => void;
    reject: (error: Error) => void;
  } | null = null;

  function ensureWorker(): BotWorkerLike {
    if (worker) return worker;
    const created = createWorker();
    created.addEventListener("message", (event) => {
      const line = event.data;
      if (typeof line !== "string" || !line.startsWith("bestmove")) return;

      const move = line.split(" ")[1]; // 'bestmove e2e4 ...'
      repliesSeen += 1;
      if (!pending || pending.id !== repliesSeen) return; // a superseded search's reply

      const settled = pending;
      pending = null;
      settled.resolve(move);
    });
    // Initialize UCI protocol
    created.postMessage("uci");
    worker = created;
    return created;
  }

  function abandonPending(reason: string) {
    if (!pending) return;
    const abandoned = pending;
    pending = null;
    abandoned.reject(new BotSearchAbandonedError(reason));
  }

  return {
    getMove(fen, level, legalMoves) {
      if (disposed) {
        return Promise.reject(
          new BotSearchAbandonedError("the engine was disposed"),
        );
      }

      abandonPending("a newer search replaced it");

      const { skillLevel, depth, randomChance } = mapEloToSearch(level);
      if (Math.random() < randomChance) {
        // Answered without the worker, so it consumes no search ordinal.
        return Promise.resolve(getRandomMove(legalMoves));
      }

      const engine = ensureWorker();
      return new Promise<string>((resolve, reject) => {
        // Only when a search is genuinely outstanding: `stop` while idle produces no `bestmove`,
        // so sending it unconditionally would be harmless but pointless, and not sending it keeps
        // the reply count trivially equal to the go count.
        if (repliesSeen < searchesStarted) engine.postMessage("stop");

        const id = ++searchesStarted;
        pending = { id, resolve, reject };

        engine.postMessage(`setoption name Skill Level value ${skillLevel}`);
        engine.postMessage(`position fen ${fen}`);
        engine.postMessage(`go depth ${depth}`);
      });
    },

    dispose() {
      if (disposed) return;
      disposed = true;
      abandonPending("the engine was disposed");
      worker?.terminate();
      worker = null;
    },
  };
}

export interface BotSearch {
  skillLevel: number;
  depth: number;
  randomChance: number;
  targetElo: number;
}

/**
 * Pure Elo -> (skillLevel, depth, randomChance) mapping (C-10-8). Keep in lockstep with
 * simulator/KiriChess.Simulator/BotSkill.cs's BotSkill.ForElo - ui/lib/bot.test.ts pins the
 * boundary values (400, 799, 800, 3000) against that C# port so the two cannot drift silently.
 */
export function mapEloToSearch(elo: number): BotSearch {
  // If level/elo is below 800, we blend in random moves to simulate true beginners
  const targetElo = Math.max(400, Math.min(3000, elo));

  let randomChance = 0;
  if (targetElo < 800) {
    // 400 ELO = 100% random, 800 ELO = 0% random
    randomChance = 1 - (targetElo - 400) / 400;
  }

  // Map Elo >= 800 to Skill Level (0 - 20).
  let skillLevel = 0;
  if (targetElo >= 800) {
    skillLevel = Math.floor(((targetElo - 800) / (3000 - 800)) * 20);
    skillLevel = Math.max(0, Math.min(20, skillLevel));
  }

  // Map Elo to Depth
  let depth = 1;
  if (targetElo < 1000) depth = 1;
  else if (targetElo < 1300) depth = 2;
  else if (targetElo < 1600) depth = 3;
  else if (targetElo < 1900) depth = 5;
  else if (targetElo < 2300) depth = 8;
  else if (targetElo < 2700) depth = 12;
  else depth = 15;

  return { skillLevel, depth, randomChance, targetElo };
}

// Fallback just in case
export function getRandomMove(legalMoves: string[]): string {
  if (legalMoves.length === 0) return "";
  const index = Math.floor(Math.random() * legalMoves.length);
  return legalMoves[index];
}
