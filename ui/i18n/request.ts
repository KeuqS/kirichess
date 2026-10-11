import { getRequestConfig } from "next-intl/server";
import { hasLocale } from "next-intl";
import { routing, type AppLocale } from "./routing";
import en from "../messages/en.json";
import nl from "../messages/nl.json";
import es from "../messages/es.json";
import fr from "../messages/fr.json";
import de from "../messages/de.json";
import ptBR from "../messages/pt-BR.json";

// Static imports, not `import(\`../messages/${locale}.json\`)`. A template is invisible to
// Turbopack's watch graph, so saving a catalogue would compile nothing and the page would keep
// serving the strings from the last server start. `Record<AppLocale, …>` is what fails the build
// when a locale is added to routing.ts without an import here.
const catalogues: Record<AppLocale, typeof en> = { en, nl, es, fr, de, "pt-BR": ptBR };

/**
 * Phase 9f.4. The locale for the current request, and its catalogue.
 *
 * The locale comes from `next/root-params` rather than next-intl's `requestLocale`, which 4.13.6
 * deprecates in favour of exactly this. That is not only a deprecation chase: reading the root
 * param is what makes static rendering work without a `setRequestLocale(locale)` call at the top of
 * every page and layout, which is the other half of the same deprecated API and would otherwise
 * have to be repeated across all 23 routes. `[locale]` is a genuine root parameter here because
 * `app/[locale]/layout.tsx` is the root layout, so the getter resolves in any server component
 * beneath it.
 *
 * The fallback is not dead code. `next/root-params` is unavailable outside the `[locale]` tree
 * (route handlers, and any render that happens before the segment is matched), and next-intl also
 * calls this config with an explicit `locale` when something asks for a specific one via
 * `getTranslations({locale})`. Both land on a valid catalogue instead of throwing.
 */
export default getRequestConfig(async ({ locale: requested }) => {
  // Both candidates go through `hasLocale`, and that matters for the second one too: `[locale]` is
  // an effective catch-all, so an unmatched path arrives here with a segment like "definitely-not-a
  // -page". Validating only the explicit override (as this did at first) let that value straight
  // into the catalogue lookup and turned every 404 into a 500 that no `notFound()` could rescue,
  // because the config throws before the layout ever renders.
  const candidate = requested ?? (await readRootLocale());
  const locale = hasLocale(routing.locales, candidate)
    ? candidate
    : routing.defaultLocale;

  return {
    locale,
    messages: catalogues[locale],
  };
});

async function readRootLocale(): Promise<string | undefined> {
  try {
    const { locale } = await import("next/root-params");
    return await locale();
  } catch {
    return undefined;
  }
}
