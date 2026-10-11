import { describe, expect, it } from "vitest";
import {
  recallBotGame,
  rememberBotGame,
  returnToGameHref,
  type RememberedBotGame,
} from "./returnToGame";

function memory() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => {
      values.set(key, value);
    },
  };
}

const gameId = "11111111-1111-4111-8111-111111111111";

describe("returnToGameHref", () => {
  it("links a rated game back to its pool", () => {
    expect(
      returnToGameHref(
        { gameId, pool: "steal1", solo: false },
        null,
      ),
    ).toBe(`/game/${gameId}?preset=steal1`);
  });

  it("links a two-player game with no pool straight to the board", () => {
    expect(
      returnToGameHref({ gameId, pool: null, solo: false }, null),
    ).toBe(`/game/${gameId}`);
  });

  it("reopens a bot game at the strength this browser started", () => {
    const remembered: RememberedBotGame = {
      gameId,
      preset: "eclipse",
      level: 750,
    };
    expect(
      returnToGameHref({ gameId, pool: null, solo: true }, remembered),
    ).toBe(`/game/${gameId}?mode=bot&level=750&preset=eclipse`);
  });

  it("still opens a bot game when this browser never saw it", () => {
    expect(
      returnToGameHref({ gameId, pool: null, solo: true }, null),
    ).toBe(`/game/${gameId}?mode=bot&level=1500`);
  });

  it("ignores a remembered bot game that is not the one still going", () => {
    expect(
      returnToGameHref(
        { gameId, pool: null, solo: true },
        { gameId: "other", preset: "steal2", level: 400 },
      ),
    ).toBe(`/game/${gameId}?mode=bot&level=1500`);
  });
});

describe("rememberBotGame", () => {
  it("round-trips the bot a player walked away from", () => {
    const storage = memory();
    rememberBotGame({ gameId, preset: "steal1", level: 750 }, storage);
    expect(recallBotGame(storage)).toEqual({
      gameId,
      preset: "steal1",
      level: 750,
    });
  });

  it("drops a stored value that is not a bot game", () => {
    const storage = memory();
    storage.setItem("kirichess.botGame", "{\"gameId\":1}");
    expect(recallBotGame(storage)).toBeNull();
  });
});
