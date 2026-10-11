import type { Metadata } from "next";
import { getLocale, getTranslations } from "next-intl/server";
import { routeMetadata } from "@/lib/seo";

// See ui/app/learn/layout.tsx, same reason (a client-component page) and the same M-1-6 /
// M-1-1 / M-1-2 fix: translated title/description plus openGraph/twitter/canonical/hreflang,
// instead of a hardcoded English literal and an inherited generic card.
// Applies to /watch/[id] too (layouts cover nested segments): a deliberate, generic fallback for
// live spectate links rather than per-game metadata, which is out of scope here; see the phase9d6
// plan doc's scope decision on live/transient pages. The canonical this adds is therefore the
// `/watch` list page's own URL even when rendered under `/watch/[id]`, which is the same
// deliberate generic-fallback tradeoff, not a new gap.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("watch"),
  ]);
  return routeMetadata({
    locale,
    path: "/watch",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

export default function WatchLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
