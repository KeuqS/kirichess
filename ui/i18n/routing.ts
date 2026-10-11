import { defineRouting } from "next-intl/routing";

/**
 * Phase 9f.4. The routing half of the i18n retrofit, deferred at extraction time and landed here
 * when the first non-English locale shipped.
 *
 * `localePrefix: "as-needed"` is the load-bearing choice, not a stylistic one. English keeps its
 * unprefixed paths (`/learn` stays `/learn`, Dutch becomes `/nl/learn`), which is what lets four
 * things that hard-code today's paths keep working untouched:
 *
 *   1. the backend's transactional mail, which builds `/verify-email`, `/reset-password` and
 *      `/game/{id}` in Infrastructure/Email/EmailTemplates.cs, some already delivered to inboxes,
 *   2. `public/sw.js`, which pins `/offline` and `/learn` (it now also understands the `/nl`
 *      prefix, but the unprefixed English paths it was written against are unchanged),
 *   3. the e2e suite's `goto` paths,
 *   4. the sitemap and canonical URLs.
 *
 * Adding a locale is this array plus a `messages/{locale}.json`, a `LOCALE_LABELS` entry, a static
 * import in `request.ts` and a `LOCALE_PREFIXES` entry in `public/sw.js`. `lib/locales.test.ts`
 * fails if any of those four fall out of step.
 *
 * ROADMAP 9f.4's rule was that a locale ships when a human who plays the variant has read it.
 * **That gate was dropped by the owner on 2026-08-24**, the same call that shipped Dutch: es, fr,
 * de and pt-BR are live here without a native reader having gone through them. What is proven of
 * all six is structure only, identical key sets and identical ICU placeholders, which
 * `locales.test.ts` now enforces on every one of them. What is not proven is meaning, and the
 * 223-key `rules` namespace is the half where a mistranslation reads as a broken game rather than
 * as a bad string. Corrections belong in `messages/{locale}.json`, and the vocabulary behind them
 * in `i18n/glossary.json`.
 */
export const routing = defineRouting({
  locales: ["en", "nl", "es", "fr", "de", "pt-BR"],
  defaultLocale: "en",
  localePrefix: "as-needed",
});

export type AppLocale = (typeof routing.locales)[number];

/** Rendered in the locale switcher, in the language itself rather than translated. */
export const LOCALE_LABELS: Record<AppLocale, string> = {
  en: "English",
  nl: "Nederlands",
  es: "Español",
  fr: "Français",
  de: "Deutsch",
  "pt-BR": "Português (BR)",
};
