"use client";

import { useTransition } from "react";
import { useLocale, useTranslations } from "next-intl";
import { usePathname, useRouter } from "@/i18n/navigation";
import { LOCALE_LABELS, routing, type AppLocale } from "@/i18n/routing";

/**
 * Ordered by the label a reader actually sees, not by `routing.locales`, which is authored
 * default-first. `localeCompare` rather than a raw `<`, so the accented labels sort where a reader
 * expects them ("Espanol" next to "English") instead of after every unaccented one.
 */
const SORTED_LOCALES = [...routing.locales].sort((a, b) =>
  LOCALE_LABELS[a].localeCompare(LOCALE_LABELS[b], "en"),
);

/**
 * Phase 9f.4. Language picker.
 *
 * A native `<select>` rather than a custom dropdown, for the reason `e2e/accessibility.spec.ts`
 * exists: the browser already gives this keyboard support, a screen-reader role and a mobile
 * picker, and none of that has to be re-proven here.
 *
 * `usePathname` is next-intl's, so it returns the route without the locale prefix ("/leaderboards",
 * never "/nl/leaderboards"), and `router.replace` puts the new locale's prefix back on. That is
 * what keeps the reader on the same page instead of dumping them at the home page, including on
 * dynamic routes, where the returned path is the concrete one ("/game/abc").
 *
 * The query string is read from `window.location` in the handler rather than through
 * `useSearchParams`, which would oblige every page rendering the footer to grow a Suspense
 * boundary. The handler only ever runs on a click, so there is no server render to disagree with.
 * Carrying the query keeps a page's own parameters (`?next=`, `?preset=`, `?accept=`) across the
 * language switch.
 */
export function LocaleSwitcher({ className = "" }: { className?: string }) {
  const t = useTranslations("LocaleSwitcher");
  const locale = useLocale();
  const pathname = usePathname();
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  function onSelect(next: AppLocale) {
    if (next === locale) return;
    const here = pathname + window.location.search;
    startTransition(() => {
      router.replace(here, { locale: next });
    });
  }

  return (
    <label className={`inline-flex items-center gap-2 ${className}`.trim()}>
      <span className="sr-only">{t("label")}</span>
      <select
        value={locale}
        disabled={isPending}
        onChange={(e) => onSelect(e.target.value as AppLocale)}
        className="input-select cursor-pointer border border-[var(--border)] bg-transparent px-2 py-1 pr-8 text-sm font-medium text-muted transition-colors hover:text-[var(--bounty)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--bounty)] disabled:cursor-wait"
      >
        {SORTED_LOCALES.map((code) => (
          <option key={code} value={code}>
            {LOCALE_LABELS[code]}
          </option>
        ))}
      </select>
    </label>
  );
}
