// Empty by default: an unconfigured environment renders no widget rather than blocking register,
// same fail-closed-to-usable pattern as NEXT_PUBLIC_GOOGLE_SIGN_IN (ui/app/login/page.tsx).
export const TURNSTILE_SITE_KEY =
  process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY ?? "";

/** P-4-5. The implicit-render poll used to reschedule forever. Ten seconds is long enough for
 * afterInteractive to land, and short enough that a blocked `challenges.cloudflare.com` is not
 * an infinite disabled button. */
export const TURNSTILE_POLL_MS = 100;
export const TURNSTILE_GIVE_UP_MS = 10_000;

export const TURNSTILE_SCRIPT_FAILED_EVENT = "kirichess:turnstile-script-failed";

export type TurnstileWaitResult = "ready" | "failed" | "cancelled";

export type TurnstileRenderOptions = {
  sitekey: string;
  action?: string;
  callback?: (token: string) => void;
  "expired-callback"?: () => void;
  "error-callback"?: () => void;
};

declare global {
  interface Window {
    turnstile?: {
      render: (
        container: HTMLElement,
        options: TurnstileRenderOptions,
      ) => string;
      reset: (widgetId: string) => void;
      remove: (widgetId: string) => void;
    };
  }
}

let turnstileScriptFailed = false;

/** P-4-5. Called from the layout `<Script onError>` so a blocked api.js can reach the login form. */
export function markTurnstileScriptFailed(): void {
  turnstileScriptFailed = true;
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event(TURNSTILE_SCRIPT_FAILED_EVENT));
  }
}

export function didTurnstileScriptFail(): boolean {
  return turnstileScriptFailed;
}

/** Clears the layout-script failure flag. Used by tests; production never needs this. */
export function resetTurnstileScriptFailed(): void {
  turnstileScriptFailed = false;
}

/**
 * P-4-5. Wait until `window.turnstile` and the widget container exist, or give up.
 * `onError` on the layout script sets the same failed result without waiting out the budget.
 */
export async function waitForTurnstileApi(options: {
  isReady: () => boolean;
  isCancelled: () => boolean;
  hasScriptFailed: () => boolean;
  now?: () => number;
  delay?: (ms: number) => Promise<void>;
  pollMs?: number;
  giveUpMs?: number;
}): Promise<TurnstileWaitResult> {
  const now = options.now ?? Date.now;
  const delay =
    options.delay ??
    ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  const pollMs = options.pollMs ?? TURNSTILE_POLL_MS;
  const giveUpMs = options.giveUpMs ?? TURNSTILE_GIVE_UP_MS;
  const started = now();

  while (true) {
    if (options.isCancelled()) return "cancelled";
    if (options.hasScriptFailed()) return "failed";
    if (options.isReady()) return "ready";
    if (now() - started >= giveUpMs) return "failed";
    await delay(pollMs);
  }
}
