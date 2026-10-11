import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

/**
 * Phase 9f.4. Locale negotiation and the `/nl` rewrite.
 *
 * This is `proxy.ts`, not `middleware.ts`: Next 16 deprecated the `middleware` file convention and
 * renamed it, and the export name moved with it. The behaviour is unchanged.
 *
 * The matcher is the canonical one, and deliberately no cleverer than that. Two things went wrong
 * here before it was, and both failed quietly rather than loudly:
 *
 *   1. Spelling out `sitemap.xml`, `robots.txt`, `manifest.webmanifest`, `sw.js` and the icon files
 *      as separate exclusions is redundant - every one of them contains a dot, so the `\\..*` clause
 *      already covers them - and the longer pattern stopped matching sub-paths at all.
 *   2. The backslash must survive into the *string*, so it is `\\.` in this source and `\.` at
 *      runtime. Written as a single backslash, JavaScript drops it, the clause degrades to `.*..*`,
 *      and that matches every path with at least one character after the slash. The proxy then runs
 *      on `/` and nothing else, so `/` works, `/nl/learn` works (it needs no rewrite), and every
 *      unprefixed English path 500s. Check any change here against a real sub-path.
 *
 * `/api` is a real exclusion rather than a tidy-up: the frontend proxies nothing, but a stray
 * rewrite there would break the SignalR negotiate call.
 */
export const proxy = createMiddleware(routing);

export const config = {
  matcher: ["/((?!api|_next|_vercel|.*\\..*).*)"],
};
