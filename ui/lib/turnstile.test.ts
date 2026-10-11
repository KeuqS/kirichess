import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  TURNSTILE_GIVE_UP_MS,
  TURNSTILE_POLL_MS,
  didTurnstileScriptFail,
  markTurnstileScriptFailed,
  resetTurnstileScriptFailed,
  waitForTurnstileApi,
} from "./turnstile";

afterEach(() => {
  resetTurnstileScriptFailed();
});

describe("waitForTurnstileApi (P-4-5)", () => {
  it("returns ready as soon as the api is present", async () => {
    const delay = vi.fn();
    await expect(
      waitForTurnstileApi({
        isReady: () => true,
        isCancelled: () => false,
        hasScriptFailed: () => false,
        delay,
      }),
    ).resolves.toBe("ready");
    expect(delay).not.toHaveBeenCalled();
  });

  it("returns failed immediately when the layout script already errored", async () => {
    const delay = vi.fn();
    await expect(
      waitForTurnstileApi({
        isReady: () => false,
        isCancelled: () => false,
        hasScriptFailed: () => true,
        delay,
      }),
    ).resolves.toBe("failed");
    expect(delay).not.toHaveBeenCalled();
  });

  it("gives up after the 10s budget if the api never appears", async () => {
    let now = 0;
    const delay = vi.fn(async (ms: number) => {
      now += ms;
    });
    await expect(
      waitForTurnstileApi({
        isReady: () => false,
        isCancelled: () => false,
        hasScriptFailed: () => false,
        now: () => now,
        delay,
      }),
    ).resolves.toBe("failed");
    expect(now).toBe(TURNSTILE_GIVE_UP_MS);
    expect(delay).toHaveBeenCalledWith(TURNSTILE_POLL_MS);
  });

  it("returns cancelled without treating it as a load failure", async () => {
    await expect(
      waitForTurnstileApi({
        isReady: () => false,
        isCancelled: () => true,
        hasScriptFailed: () => false,
        delay: vi.fn(),
      }),
    ).resolves.toBe("cancelled");
  });
});

describe("markTurnstileScriptFailed (P-4-5)", () => {
  it("sets the flag the login poll reads", () => {
    expect(didTurnstileScriptFail()).toBe(false);
    markTurnstileScriptFailed();
    expect(didTurnstileScriptFail()).toBe(true);
  });
});

describe("Turnstile script placement (L-1-6)", () => {
  const uiRoot = join(__dirname, "..");

  it("mounts the script on the login page and not in the locale layout", () => {
    const layout = readFileSync(join(uiRoot, "app/[locale]/layout.tsx"), "utf8");
    const login = readFileSync(join(uiRoot, "app/[locale]/login/page.tsx"), "utf8");
    expect(layout).not.toContain("TurnstileScript");
    expect(login).toContain("<TurnstileScript />");
  });
});
