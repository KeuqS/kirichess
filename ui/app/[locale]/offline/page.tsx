import type { Metadata } from "next";
import { Link } from "@/i18n/navigation";
import { getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("offline");
  return {
    title: t("title"),
    robots: { index: false, follow: false },
  };
}

/**
 * The service worker's fallback for any navigation that fails while offline. Deliberately never a
 * cached game/watch/replay page: the clock is server-authoritative (Phase 3), so a page that looked
 * playable with no connection would be worse than this honest notice. `/learn` is the one route that
 * genuinely works offline once its assets are cached, so it's the one link out of here.
 */
export default async function OfflinePage() {
  const t = await getTranslations("offline");
  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex-1 flex items-center justify-center px-6 py-16 sm:px-10 sm:py-24 scroll-mt-24"
      >
        <div className="page-card flex flex-col items-center text-center gap-4">
          <h1 className="text-2xl font-bold tracking-tight text-white text-balance">
            {t("heading")}
          </h1>
          <p className="text-muted">{t("body")}</p>
          <div className="flex flex-wrap items-center justify-center gap-3 pt-2">
            {/* eslint-disable-next-line @next/next/no-html-link-for-pages -- a real navigation, not
                a client-side <Link>, is what re-runs the service worker's network-first fetch for "/" */}
            <a href="/" className="btn-primary px-5 py-2 text-sm">
              {t("tryAgain")}
            </a>
            <Link href="/learn" className="btn-secondary px-5 py-2 text-sm">
              {t("practiceRules")}
            </Link>
          </div>
        </div>
      </main>
      <Footer />
    </div>
  );
}
