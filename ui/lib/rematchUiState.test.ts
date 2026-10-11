import { describe, expect, it } from "vitest";
import { rematchUiState } from "./rematchUiState";

describe("rematchUiState", () => {
  const me = "player-a";
  const them = "player-b";

  it("is idle when nobody has offered", () => {
    expect(rematchUiState(me, null)).toBe("idle");
    expect(rematchUiState(me, undefined)).toBe("idle");
  });

  it("is offered when this viewer made the standing offer", () => {
    expect(rematchUiState(me, me)).toBe("offered");
  });

  it("is incoming when the opponent made the standing offer", () => {
    expect(rematchUiState(me, them)).toBe("incoming");
  });

  it("is idle when the viewer is not signed in yet", () => {
    expect(rematchUiState(null, them)).toBe("idle");
  });
});
