const dsn = process.env.NEXT_PUBLIC_SENTRY_DSN;

/**
 * P-7-4. The client SDK is a no-op until NEXT_PUBLIC_SENTRY_DSN is baked in at build
 * time. A static `import * as Sentry` would still put ~30-40 KB gzip on every page.
 */
export const onRouterTransitionStart = (
  ...args: unknown[]
): void => {
  if (!dsn) return;
  void import("@sentry/nextjs").then((Sentry) => {
    Sentry.captureRouterTransitionStart(
      ...(args as Parameters<typeof Sentry.captureRouterTransitionStart>),
    );
  });
};

if (dsn) {
  void import("@sentry/nextjs").then((Sentry) => {
    Sentry.init({
      dsn,
      environment:
        process.env.NEXT_PUBLIC_SENTRY_ENVIRONMENT ?? process.env.NODE_ENV,
      tracesSampleRate: 0,
      sendDefaultPii: false,
      beforeSend(event, hint) {
        const original = hint?.originalException as { name?: string } | undefined;
        return original?.name === "ApiError" ? null : event;
      },
    });
  });
}
