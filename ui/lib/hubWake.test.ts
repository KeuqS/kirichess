import { HubConnectionState } from "@microsoft/signalr";
import { describe, expect, it, vi } from "vitest";
import { wakeDisconnectedHub } from "./hubWake";

describe("wakeDisconnectedHub (P-6-5)", () => {
  it("skips when the tab is not visible", async () => {
    const start = vi.fn(async () => undefined);
    await expect(
      wakeDisconnectedHub({
        visibilityState: "hidden",
        hubState: HubConnectionState.Disconnected,
        start,
      }),
    ).resolves.toBe("skipped");
    expect(start).not.toHaveBeenCalled();
  });

  it("skips when the hub is already connected or still retrying", async () => {
    const start = vi.fn(async () => undefined);
    await expect(
      wakeDisconnectedHub({
        visibilityState: "visible",
        hubState: HubConnectionState.Connected,
        start,
      }),
    ).resolves.toBe("skipped");
    await expect(
      wakeDisconnectedHub({
        visibilityState: "visible",
        hubState: HubConnectionState.Reconnecting,
        start,
      }),
    ).resolves.toBe("skipped");
    await expect(
      wakeDisconnectedHub({
        visibilityState: "visible",
        hubState: HubConnectionState.Connecting,
        start,
      }),
    ).resolves.toBe("skipped");
    expect(start).not.toHaveBeenCalled();
  });

  it("starts a disconnected hub when the tab returns", async () => {
    const start = vi.fn(async () => undefined);
    await expect(
      wakeDisconnectedHub({
        visibilityState: "visible",
        hubState: HubConnectionState.Disconnected,
        start,
      }),
    ).resolves.toBe("started");
    expect(start).toHaveBeenCalledTimes(1);
  });

  it("returns failed when start rejects", async () => {
    const start = vi.fn(async () => {
      throw new Error("negotiate failed");
    });
    await expect(
      wakeDisconnectedHub({
        visibilityState: "visible",
        hubState: HubConnectionState.Disconnected,
        start,
      }),
    ).resolves.toBe("failed");
  });
});
