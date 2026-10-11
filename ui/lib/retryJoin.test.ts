import { describe, expect, it, vi } from "vitest";
import { retryJoin } from "./retryJoin";

describe("retryJoin (P-4-7)", () => {
  it("returns ok on the first attempt without delaying", async () => {
    const join = vi.fn().mockResolvedValue(undefined);
    const delay = vi.fn();
    await expect(retryJoin(join, { delay })).resolves.toBe("ok");
    expect(join).toHaveBeenCalledOnce();
    expect(delay).not.toHaveBeenCalled();
  });

  it("retries twice after a failure and returns ok when a later attempt succeeds", async () => {
    const join = vi
      .fn()
      .mockRejectedValueOnce(new Error("blip"))
      .mockRejectedValueOnce(new Error("blip"))
      .mockResolvedValueOnce(undefined);
    const delay = vi.fn().mockResolvedValue(undefined);
    await expect(retryJoin(join, { delay, delayMs: 10 })).resolves.toBe("ok");
    expect(join).toHaveBeenCalledTimes(3);
    expect(delay).toHaveBeenCalledTimes(2);
    expect(delay).toHaveBeenNthCalledWith(1, 10);
    expect(delay).toHaveBeenNthCalledWith(2, 10);
  });

  it("returns failed after the first try plus the extra attempts all reject", async () => {
    const join = vi.fn().mockRejectedValue(new Error("still down"));
    const delay = vi.fn().mockResolvedValue(undefined);
    await expect(
      retryJoin(join, { delay, extraAttempts: 2 }),
    ).resolves.toBe("failed");
    expect(join).toHaveBeenCalledTimes(3);
  });

  it("stops as cancelled without further attempts", async () => {
    const join = vi.fn().mockRejectedValue(new Error("blip"));
    const delay = vi.fn().mockResolvedValue(undefined);
    let cancelled = false;
    const result = retryJoin(join, {
      delay,
      isCancelled: () => cancelled,
    });
    cancelled = true;
    await expect(result).resolves.toBe("cancelled");
  });
});
