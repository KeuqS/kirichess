/**
 * P-4-7. `join()` inside `onreconnected` used to fail once and print a terminal sentence.
 * Retry a couple of times on a short delay before the caller surfaces anything.
 *
 * The first attempt is immediate. `extraAttempts` (default 2) are the delayed retries the
 * packet asked for, so a blip that recovers inside a second never becomes a Reload prompt.
 */
export const JOIN_RETRY_DELAY_MS = 400;
export const JOIN_RETRY_EXTRA_ATTEMPTS = 2;

export type RetryJoinResult = "ok" | "cancelled" | "failed";

export async function retryJoin(
  join: () => Promise<void>,
  options?: {
    extraAttempts?: number;
    delayMs?: number;
    delay?: (ms: number) => Promise<void>;
    isCancelled?: () => boolean;
  },
): Promise<RetryJoinResult> {
  const extra = options?.extraAttempts ?? JOIN_RETRY_EXTRA_ATTEMPTS;
  const delayMs = options?.delayMs ?? JOIN_RETRY_DELAY_MS;
  const delay =
    options?.delay ??
    ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  const isCancelled = options?.isCancelled ?? (() => false);
  const attempts = extra + 1;

  for (let i = 0; i < attempts; i++) {
    if (isCancelled()) return "cancelled";
    try {
      await join();
      return "ok";
    } catch {
      if (i === attempts - 1) return "failed";
      await delay(delayMs);
    }
  }

  return "failed";
}
