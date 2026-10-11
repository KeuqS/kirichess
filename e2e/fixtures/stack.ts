import { execFileSync } from "node:child_process";
import * as path from "node:path";

/** Repo root: this file is e2e/fixtures/, so two levels up. */
export const REPO_ROOT = path.resolve(__dirname, "..", "..");

export const COMPOSE = [
  "compose",
  "-f",
  "docker-compose.prod.yml",
  "-f",
  "docker-compose.e2e.yml",
];

export const API_BASE = "http://localhost:8080";
export const APP_BASE = "http://localhost:3000";

/** Raw stdout of the backend container. 64 MB ceiling, a long run logs every SignalR event. */
export function backendLogs(): string {
  return execFileSync(
    "docker",
    [...COMPOSE, "logs", "--no-color", "--no-log-prefix", "backend"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      maxBuffer: 64 * 1024 * 1024,
    },
  );
}

const VERIFY_LINK =
  /https?:\/\/[^\s"'\\]+\/verify-email\?userId=[^\s"'\\&]+&token=[^\s"'\\]+/;

/**
 * The confirmation link for one address, read out of the backend's stdout.
 *
 * This is not a shortcut around the flow. With no Email:ApiKey configured the registered sender is
 * LoggingEmailSender, whose own doc comment states the contract this relies on: it logs the
 * plain-text part, "which by contract contains the full confirmation/reset URL". That URL is built
 * by the same EmailTemplates.Confirmation call a real send would use, so following it exercises
 * /verify-email and POST /api/auth/confirm-email for real.
 *
 * Outside Development the log provider is AddJsonConsole (Program.cs), i.e. one JSON object per
 * line. The line is PARSED rather than regexed, because System.Text.Json's default encoder escapes
 * '&' as \u0026, a regex over the raw text would need to match both forms, and JSON.parse simply
 * gives back the real string.
 */
export async function confirmationLinkFor(
  email: string,
  timeoutMs = 30_000,
): Promise<string> {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    for (const line of backendLogs().split(/\r?\n/)) {
      if (!line.includes(email)) continue;

      let body: string | undefined;
      try {
        const entry = JSON.parse(line) as {
          State?: Record<string, unknown>;
          Message?: string;
        };
        const state = entry.State ?? {};
        if (typeof state.ToEmail === "string" && state.ToEmail !== email)
          continue;
        body =
          (typeof state.Body === "string" ? state.Body : undefined) ??
          (typeof entry.Message === "string" ? entry.Message : undefined);
      } catch {
        // Not a JSON line (a raw stack trace, a startup banner). Fall back to the raw text and
        // undo the one escape that matters.
        body = line.replace(/\\u0026/gi, "&");
      }

      const match = body ? VERIFY_LINK.exec(body) : null;
      if (match) return match[0];
    }
    await new Promise((r) => setTimeout(r, 500));
  }

  throw new Error(
    `No /verify-email link for ${email} appeared in the backend log within ${timeoutMs}ms. ` +
      `Check that Frontend__BaseUrl is set and Email__ApiKey is NOT (otherwise EmailOptions.Enabled ` +
      `is true and the mail was handed to Resend instead of the log). ` +
      `Run: node scripts/stack.mjs logs`,
  );
}

/** Polls until `check` succeeds or the deadline passes. Used only by global-setup. */
export async function waitFor(
  label: string,
  check: () => Promise<boolean>,
  timeoutMs = 120_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError = "";
  while (Date.now() < deadline) {
    try {
      if (await check()) return;
    } catch (err) {
      lastError = err instanceof Error ? err.message : String(err);
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(
    `${label} was not ready within ${timeoutMs}ms. Last error: ${lastError || "none"}`,
  );
}
