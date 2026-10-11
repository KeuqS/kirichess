import { notFound } from "next/navigation";

/**
 * Phase 9f.4. Unmatched paths.
 *
 * With every route under `[locale]`, the root layout lives there too, so there is no layout above
 * `app/` for a root-level `not-found.tsx` to render inside. The proxy rewrites any unmatched path
 * into the locale segment (`/nonsense` becomes `/en/nonsense`), this catch-all receives it, and
 * `notFound()` hands off to `app/[locale]/not-found.tsx` - which renders inside the real layout, so
 * a 404 keeps the nav, the footer and the reader's locale. Next 16's `global-not-found.tsx` would
 * be the other way to do this; it is still flagged experimental and needs a config flag, and it
 * bypasses the layout by design, which is the opposite of what is wanted here.
 */
export default function CatchAllNotFound() {
  notFound();
}
