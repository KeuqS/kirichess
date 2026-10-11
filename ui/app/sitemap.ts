import type { MetadataRoute } from "next";
import { routing } from "@/i18n/routing";
import { localizedUrl } from "@/lib/seo";

// Deliberately just the pages robots.ts allows *and* that carry real, stable, ranking-worthy
// content: the same "growth/acquisition" set the phase9d6 plan doc reasons about (/rules and
// /learn are the two named in the roadmap item itself). /login and /forgot-password are allowed by
// robots.ts (nothing sensitive in the URL) but have no unique content worth ranking, so they're
// left out here rather than padding the sitemap.
const ROUTES: { path: string; priority: number }[] = [
  { path: "/", priority: 1.0 },
  { path: "/rules", priority: 0.8 },
  { path: "/learn", priority: 0.8 },
  { path: "/leaderboards", priority: 0.6 },
  { path: "/watch", priority: 0.6 },
  { path: "/about", priority: 0.5 },
  { path: "/developers", priority: 0.5 },
  { path: "/donate", priority: 0.3 },
  { path: "/privacy", priority: 0.1 },
  { path: "/terms", priority: 0.1 },
];

// One entry per page (at the default locale's URL) carrying an `alternates.languages` map, rather
// than one flat entry per locale per page. That is what tells a crawler these are the same document
// in different languages instead of near-duplicate content competing with itself, which is the same
// dilution argument robots.ts already makes about /replay/* and /watch/*.
//
// M-1-7: no `lastModified` field. It used to be `new Date()` on every entry, which stamped the
// whole tree as "modified now" on every deploy regardless of whether a route's content actually
// changed - noise a crawler learns to discount, not a signal. A real per-route date would have to
// come from git history (the page file's last commit), but Dockerfile.frontend's builder stage
// only `COPY ui/ ./`s the frontend in - no `.git` is present in that build context, so `git log`
// can't run at build time in the image that actually ships (the same constraint documented on
// privacy/page.tsx and terms/page.tsx for why they hand-render their source markdown instead of
// reading it). A hardcoded per-route constant would need someone to remember to bump it by hand on
// every content change, which is a second, higher-maintenance kind of noise. Omitting the field
// entirely is what the finding itself calls "the smaller change", and Google's own guidance is that
// a sitemap without `lastmod` is fine for a small, mostly-static site like this one.
export default function sitemap(): MetadataRoute.Sitemap {
  return ROUTES.map(({ path, priority }) => ({
    url: localizedUrl(routing.defaultLocale, path),
    priority,
    alternates: {
      languages: Object.fromEntries(
        routing.locales.map((locale) => [locale, localizedUrl(locale, path)]),
      ),
    },
  }));
}
