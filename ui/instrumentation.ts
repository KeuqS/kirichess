import * as Sentry from "@sentry/nextjs";

/** Next calls this once per server runtime, before any request is handled. */
export async function register() {
  if (process.env.NEXT_RUNTIME === "nodejs") {
    await import("./sentry.server.config");
  }
  if (process.env.NEXT_RUNTIME === "edge") {
    await import("./sentry.edge.config");
  }
}

// Captures errors thrown inside React Server Components, which do not surface to any error
// boundary the browser can see. No-op when init was never called.
export const onRequestError = Sentry.captureRequestError;
