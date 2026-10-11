import type { Metadata } from "next";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getMessages, getTranslations } from "next-intl/server";
import { routeMetadata } from "@/lib/seo";
import { LEARN_CLIENT_NAMESPACES, pickMessages } from "@/lib/clientMessages";

// `page.tsx` in this directory is a client component (interactive tutorial), which can't export
// `metadata` itself: App Router requires a server component for that. This layout is the
// server-component sibling that carries it; see Phase 9d.6
//
// M-1-6: title/description used to be hardcoded English literals here, unlike rules/about/donate's
// translated `metaTitle`/`metaDescription`, so a Dutch reader on `/nl/learn` got an English
// `<title>`. Now pulled from `learn.metaTitle`/`learn.metaDescription` (ui/messages/*.json) like
// its peers. M-1-1 / M-1-2: `routeMetadata` also adds the openGraph/twitter card and
// canonical/hreflang this layout was missing, same fix as about/page.tsx.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("learn"),
  ]);
  return routeMetadata({
    locale,
    path: "/learn",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

export default async function LearnLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const messages = pickMessages(
    (await getMessages()) as Record<string, unknown>,
    LEARN_CLIENT_NAMESPACES,
  );
  return (
    <NextIntlClientProvider messages={messages}>{children}</NextIntlClientProvider>
  );
}
