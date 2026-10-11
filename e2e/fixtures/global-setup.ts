import { API_BASE, APP_BASE, waitFor } from "./stack";

/**
 * Waits for a stack somebody else started (the CI workflow, or `npm run stack:up`). Deliberately
 * does NOT run `docker compose up` itself: a stack that fails to build should fail on its own
 * visible step with its own logs, not inside a Playwright global setup.
 */
export default async function globalSetup(): Promise<void> {
  await waitFor("backend /health/ready", async () => {
    const res = await fetch(`${API_BASE}/health/ready`);
    if (!res.ok) return false;
    return ((await res.json()) as { status?: string }).status === "ready";
  });

  await waitFor("frontend", async () => (await fetch(APP_BASE)).ok);
}
