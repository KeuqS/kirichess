import { describe, expect, it } from "vitest";
import { GAME_HUB_RECONNECT_DELAYS_MS } from "./realtime";

describe("GAME_HUB_RECONNECT_DELAYS_MS (P-6-5)", () => {
  it("uses the longer seven-step schedule from the packet, not SignalR's four-try default", () => {
    expect([...GAME_HUB_RECONNECT_DELAYS_MS]).toEqual([
      0, 2000, 5000, 10000, 30000, 30000, 60000,
    ]);
  });
});
