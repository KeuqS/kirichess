import { createNavigation } from "next-intl/navigation";
import { routing } from "./routing";

/**
 * Locale-aware replacements for `next/link` and `next/navigation`. Under `localePrefix:
 * "as-needed"` a plain `next/link` href would silently drop the prefix, so a Dutch reader clicking
 * a nav item would land back on the English page. Import `Link`, `useRouter`, `usePathname` and
 * `redirect` from here rather than from `next/*` anywhere inside `app/[locale]`.
 *
 * `useSearchParams`, `useParams` and `notFound` are not locale-aware and keep coming from
 * `next/navigation`.
 */
export const { Link, redirect, usePathname, useRouter, getPathname } =
  createNavigation(routing);
