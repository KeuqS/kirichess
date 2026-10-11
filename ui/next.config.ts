import type { NextConfig } from "next";
import { withSentryConfig } from "@sentry/nextjs";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

// Phase 9c.1. `headers()` is evaluated once, at `next build` time, into the routes manifest that
// the standalone server then serves from - not per-request. That's the right lifetime here: these
// values are derived from NEXT_PUBLIC_* env vars, which Dockerfile.frontend already bakes in at
// build time (Next inlines them into the client bundle then too), so a config-time read stays
// consistent with what shipped in the JS. It also means these headers land on *every* response,
// static or dynamic - which matters, because most routes in this app are statically prerendered.
//
// That static/dynamic mix is also why script-src takes 'unsafe-inline' rather than a per-request
// nonce: the App Router's own inline hydration payload (`self.__next_f.push(...)`) ships on every
// page, and a nonce minted by middleware only reaches pages actually rendered per-request - it
// never reaches a prerendered static page's cached HTML, whose inline script would then be missing
// the nonce and get silently blocked. A nonce'd CSP looks stricter but breaks hydration on most of
// this app's routes; 'unsafe-inline' is the version that doesn't.
function apiOrigins(): string[] {
  // Same fallback as ui/lib/api.ts. Sentry and Turnstile fail closed when unset because the client
  // then makes no request; the API always has a destination (localhost in local dev), so omitting
  // it from connect-src blocks PresenceProvider's negotiate and surfaces as TypeError: Failed to
  // fetch (localhost:5053) on every page, including ones that never call the REST API.
  const base = process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5053";
  try {
    const origin = new URL(base).origin;
    // SignalR's websocket transport (ui/lib/realtime.ts) connects to the same origin as the REST
    // API, just over ws(s) instead of http(s) - both are needed in connect-src.
    return [origin, origin.replace(/^http/, "ws")];
  } catch {
    return [];
  }
}

// An empty DSN contributes nothing to connect-src (see ui/instrumentation-client.ts).
// Production bakes one in fly.ui.toml; local and staging leave it empty. Same fail-closed
// shape as every other optional integration in this codebase.
function sentryOrigin(): string[] {
  const dsn = process.env.NEXT_PUBLIC_SENTRY_DSN;
  if (!dsn) return [];
  try {
    return [new URL(dsn).origin];
  } catch {
    return [];
  }
}

// DH.3. https://challenges.cloudflare.com serves the widget script (script-src), the challenge
// iframe (frame-src), and that script's own XHR/fetch calls (connect-src). The API redeems the
// token; the old siteverify Worker is not called. Gated on the site key like every other optional
// origin here: unconfigured means unreachable.
const turnstileOrigin = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY
  ? ["https://challenges.cloudflare.com"]
  : [];

function buildCsp(): string {
  const connectSrc = [
    "'self'",
    ...apiOrigins(),
    ...sentryOrigin(),
    ...turnstileOrigin,
  ];

  // Fast Refresh talks over a WebSocket to this origin. `'self'` covers http(s) of the page, but
  // not `ws:` in every browser, so without this a save looks like a no-op. The scheme form (not a
  // host) is what still works when the tab is on the LAN URL `next dev` prints, not just localhost.
  if (process.env.NODE_ENV !== "production") connectSrc.push("ws:");

  // Next.js dev mode's Fast Refresh runtime applies hot-reloaded modules via eval(), which throws
  // under a script-src without 'unsafe-eval' - the failure lands as a client-side EvalError before
  // hydration finishes, so React never attaches event handlers and every button on the site goes
  // inert (not just a broken HMR overlay). The production bundle never calls eval(), so this stays
  // off there, same guard direction as upgrade-insecure-requests/HSTS below.
  const scriptSrcOrigins = ["'self'", "'unsafe-inline'", ...turnstileOrigin];
  // 'wasm-unsafe-eval' lets the Stockfish worker compile its .wasm module. It does not
  // permit eval(). Dev already has 'unsafe-eval' for Fast Refresh, which covers wasm there.
  const scriptSrc =
    process.env.NODE_ENV === "production"
      ? `script-src ${scriptSrcOrigins.join(" ")} 'wasm-unsafe-eval'`
      : `script-src ${scriptSrcOrigins.join(" ")} 'unsafe-eval'`;

  const directives = [
    `default-src 'self'`,
    scriptSrc,
    // Inline style attributes (React's style={{...}}, used throughout ui/components) need this too
    // - CSP has no nonce/hash mechanism for the style attribute, only for <style> elements.
    `style-src 'self' 'unsafe-inline'`,
    // ui/lib/bot.ts loads ui/public/stockfish-19-lite-single.js as a same-origin Worker. The
    // worker then fetches the sibling .wasm. worker-src falls back to script-src if unset, which
    // would work here too, but naming it explicitly means a future, stricter script-src can't
    // silently take the bot engine down with it.
    `worker-src 'self'`,
    `img-src 'self' data: blob: ${apiOrigins().join(" ")}`.trim(),
    `font-src 'self'`,
    `connect-src ${connectSrc.join(" ")}`,
    // DH.3. The Turnstile challenge itself renders in an iframe from this origin; with no frame-src
    // named, it would fall back to default-src 'self' and get silently blocked. No entry at all
    // (rather than 'none') when the site key is unset, since there is then nothing to permit.
    ...(turnstileOrigin.length > 0
      ? [`frame-src ${turnstileOrigin.join(" ")}`]
      : []),
    `object-src 'none'`,
    `base-uri 'self'`,
    `form-action 'self'`,
    // The concrete risk this phase names: /moderation carries ban buttons and must never be
    // framable. X-Frame-Options: DENY (below) is the same rule for browsers that predate this.
    `frame-ancestors 'none'`,
  ];

  // Only in production: upgrading a plain http://localhost dev request to https breaks it outright.
  if (process.env.NODE_ENV === "production")
    directives.push("upgrade-insecure-requests");

  return directives.join("; ");
}

const nextConfig: NextConfig = {
  // Only needed for the production image (Dockerfile.frontend copies .next/standalone out of the
  // builder stage). Left on for `next dev` too, it tries to manage that folder on startup and, on a
  // Windows bind mount, can EACCES on a stale standalone dir from a prior build and crash the dev
  // server.
  output: process.env.NODE_ENV === "production" ? "standalone" : undefined,
  async headers() {
    const headers = [
      { key: "Content-Security-Policy", value: buildCsp() },
      { key: "X-Frame-Options", value: "DENY" },
      { key: "X-Content-Type-Options", value: "nosniff" },
      { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
    ];

    // Meaningless (and ignored by browsers) over plain http, and actively dangerous in local dev -
    // a browser that ever saw this on http://localhost would force https there for the max-age
    // duration. Restrict it to production, same guard as upgrade-insecure-requests above.
    if (process.env.NODE_ENV === "production") {
      headers.push({
        key: "Strict-Transport-Security",
        value: "max-age=63072000; includeSubDomains",
      });
    }

    return [
      ...["/stockfish-19-lite-single.js", "/stockfish-19-lite-single.wasm"].map(
        (source) => ({
          source,
          headers: [
            ...headers,
            {
              key: "Cache-Control",
              value: "public, max-age=31536000, immutable",
            },
          ],
        }),
      ),
      { source: "/(.*)", headers },
    ];
  },
};

const withIntl = withNextIntl(nextConfig);

// P-7-4: withSentryConfig stays off until NEXT_PUBLIC_SENTRY_DSN is set at image-build
// time. Production sets it in fly.ui.toml; an empty value (local, staging) must not wrap
// the build (see DEPLOYMENT.md).
export default process.env.NEXT_PUBLIC_SENTRY_DSN
  ? withSentryConfig(withIntl, {
      silent: true,
      sourcemaps: { disable: true },
      disableLogger: true,
    })
  : withIntl;
