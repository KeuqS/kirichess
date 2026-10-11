/**
 * How a player gets back to the board they left.
 *
 * The server refuses a second game with the English sentence {@link ALREADY_IN_GAME_MESSAGE}
 * (PlayEligibilityPolicy). It does not name the game. GET /api/games/playing does, and a bot's
 * strength only exists in this browser: the server has no "bot", so the level has to be remembered
 * here or a return link would reopen the board with nobody to move the other side.
 */

/** Verbatim default from PlayEligibilityPolicy.EnsureNotAlreadyInGameAsync. */
export const ALREADY_IN_GAME_MESSAGE = "You're already in a game.";

export interface CurrentGameLink {
  gameId: string;
  pool: string | null;
  solo: boolean;
}

export interface RememberedBotGame {
  gameId: string;
  preset: string;
  level: number;
}

interface BotGameStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

const STORAGE_KEY = "kirichess.botGame";

/** Dialog default. Used when this browser never saw the bot game, so a return still has an opponent. */
const DEFAULT_BOT_LEVEL = 1500;

function browserStorage(): BotGameStorage | null {
  try {
    if (typeof localStorage === "undefined") return null;
    return localStorage;
  } catch {
    return null;
  }
}

export function rememberBotGame(
  game: RememberedBotGame,
  storage: BotGameStorage | null = browserStorage(),
): void {
  try {
    storage?.setItem(STORAGE_KEY, JSON.stringify(game));
  } catch {
    // Private mode can throw on setItem. The return link still opens the board, at the default strength.
  }
}

export function recallBotGame(
  storage: Pick<BotGameStorage, "getItem"> | null = browserStorage(),
): RememberedBotGame | null {
  try {
    const raw = storage?.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<RememberedBotGame>;
    if (
      typeof parsed.gameId !== "string" ||
      typeof parsed.preset !== "string" ||
      typeof parsed.level !== "number" ||
      !Number.isFinite(parsed.level)
    ) {
      return null;
    }
    return {
      gameId: parsed.gameId,
      preset: parsed.preset,
      level: parsed.level,
    };
  } catch {
    return null;
  }
}

export function returnToGameHref(
  game: CurrentGameLink,
  remembered: RememberedBotGame | null,
): string {
  const params = new URLSearchParams();
  const rememberedThis =
    remembered !== null && remembered.gameId === game.gameId
      ? remembered
      : null;
  if (game.solo) {
    params.set("mode", "bot");
    params.set(
      "level",
      String(rememberedThis ? rememberedThis.level : DEFAULT_BOT_LEVEL),
    );
  }
  const preset = rememberedThis?.preset ?? game.pool;
  if (preset) params.set("preset", preset);
  const qs = params.toString();
  return qs ? `/game/${game.gameId}?${qs}` : `/game/${game.gameId}`;
}
