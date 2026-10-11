import type { Metadata } from "next";
import { routing } from "../i18n/routing";
import { SITE_URL } from "./site";

/**
 * M-1-2 / M-1-5. `localePrefix: "as-needed"` (see `i18n/routing.ts`) means English is unprefixed
 * and every other locale carries its code, so a canonical or hreflang link built without this
 * would point at a locale-prefixed English URL (a 404) or an unprefixed non-English one (the
 * wrong document). This mirrors `app/sitemap.ts`'s own `href()`, which has the same asymmetry to
 * account for; the two are kept in sync by having `sitemap.ts` import this instead of a second
 * copy of the logic.
 */
export function localizedUrl(locale: string, path: string): string {
  const prefix = locale === routing.defaultLocale ? "" : `/${locale}`;
  // "/" would otherwise become "/nl/", a different URL to the one the proxy serves.
  const suffix = path === "/" ? "" : path;
  return `${SITE_URL}${prefix}${suffix}` || `${SITE_URL}/`;
}

/**
 * Canonical + hreflang alternates for one page: `canonical` is the current locale's own URL for
 * `path`, `languages` is every locale's URL for that same `path` plus an `x-default` pointing at
 * the unprefixed English URL, so a crawler with no locale-fallback signal has one nominated
 * (M-1-5). `path` is locale-independent, e.g. `"/rules"` or `"/"` for the home page.
 *
 * Typed narrower than `Metadata["alternates"]` on purpose: that type leaves `languages` optional
 * (any `alternates` caller might skip it), but this function always sets it, and callers that
 * destructure `.languages` off the result shouldn't have to re-narrow an `undefined` this function
 * never produces.
 */
export function pageAlternates(
  locale: string,
  path: string,
): { canonical: string; languages: Record<string, string> } {
  return {
    canonical: localizedUrl(locale, path),
    languages: {
      "x-default": localizedUrl(routing.defaultLocale, path),
      ...Object.fromEntries(
        routing.locales.map((l) => [l, localizedUrl(l, path)]),
      ),
    },
  };
}

/**
 * Full metadata for one localized, translated content page: title, description, a self-referencing
 * canonical, hreflang alternates, and an openGraph/twitter card carrying the same title/description
 * plus the page's own URL. Without the explicit `openGraph`/`twitter` here, Next's shallow-per-key
 * metadata merge means a page that only sets `title`/`description` inherits the *whole* parent
 * object instead (M-1-1) - `replay/[id]/layout.tsx` and `challenge/[token]/layout.tsx` hit this
 * first and this helper is the same fix generalised for pages whose title/description are static
 * per locale rather than fetched per object.
 */
export function routeMetadata({
  locale,
  path,
  title,
  description,
}: {
  locale: string;
  path: string;
  title: string;
  description: string;
}): Metadata {
  const url = localizedUrl(locale, path);
  return {
    title,
    description,
    alternates: pageAlternates(locale, path),
    openGraph: { title, description, url, type: "website" },
    twitter: { title, description },
  };
}
