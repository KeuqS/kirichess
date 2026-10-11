import * as Sentry from "@sentry/nextjs";

// This file exists because Next initialises the edge runtime separately (middleware and edge
// routes never execute sentry.server.config.ts); without it, Sentry silently reports nothing from
// edge code. This app has no middleware today, so this looks redundant until someone adds one.
//
// SENTRY_DSN is a runtime env var (read at cold-start); the NEXT_PUBLIC_ fallback exists so a
// single build arg configures both halves when that is all someone sets. No DSN means init is
// never called at all, so an unconfigured deploy does no network work rather than run a
// disabled SDK. Production sets the DSN; empty is the local and staging path.
const dsn = process.env.SENTRY_DSN ?? process.env.NEXT_PUBLIC_SENTRY_DSN;

if (dsn) {
  Sentry.init({
    dsn,
    environment:
      process.env.SENTRY_ENVIRONMENT ??
      process.env.NEXT_PUBLIC_SENTRY_ENVIRONMENT ??
      process.env.NODE_ENV,
    // Errors only. Performance monitoring, profiling and session replay are out of scope for 6d.
    tracesSampleRate: 0,
    sendDefaultPii: false,
    beforeSend(event, hint) {
      // ApiError is how lib/api.ts signals an expected non-2xx (bad password, taken email, 404).
      // Reporting those would bury real defects under normal user behaviour.
      const original = hint?.originalException as { name?: string } | undefined;
      return original?.name === "ApiError" ? null : event;
    },
  });
}
