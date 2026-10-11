import type { Metadata } from "next";
import { getLocale, getTranslations } from "next-intl/server";
import { routeMetadata } from "@/lib/seo";

// See ui/app/learn/layout.tsx, same reason (a client-component page) and the same M-1-6 /
// M-1-1 / M-1-2 fix: translated title/description plus openGraph/twitter/canonical/hreflang,
// instead of a hardcoded English literal and an inherited generic card.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("leaderboards"),
  ]);
  return routeMetadata({
    locale,
    path: "/leaderboards",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

export default function LeaderboardsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
