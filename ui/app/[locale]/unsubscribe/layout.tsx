import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";

// page.tsx in this directory is "use client" (it reads useSearchParams and fires the unsubscribe
// call itself), which can't export `metadata`/`generateMetadata`: App Router requires a server
// component for that, same reason learn/layout.tsx and leaderboards/layout.tsx exist. M-1-3: this
// URL carries a one-time token as a query param and fires the state change on load, the same shape
// robots.ts already disallows /reset-password and /verify-email for - `robots: { index: false,
// follow: false }` here is the belt to robots.txt's suspenders, since robots.txt is advisory and an
// unfurler that fetches the link anyway would otherwise still index a stale, token-bearing URL.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("unsubscribe");
  return {
    title: t("metaTitle"),
    robots: { index: false, follow: false },
  };
}

export default function UnsubscribeLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
