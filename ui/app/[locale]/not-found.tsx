import type { Metadata } from "next";
import { Link } from "@/i18n/navigation";
import { getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { NotFoundScene } from "@/components/NotFoundScene";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("notFound");
  return {
    title: t("title"),
    robots: { index: false, follow: false },
  };
}

export default async function NotFound() {
  const t = await getTranslations("notFound");
  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex-1 flex items-center justify-center px-6 py-16 sm:px-10 sm:py-24 scroll-mt-24"
      >
        <div className="page-card flex flex-col items-center text-center">
          <NotFoundScene />
          <p className="mt-4 m-0 text-2xl font-bold tracking-tight text-[var(--bounty)]">
            {t("code")}
          </p>
          <h1 className="m-0 mt-2 text-2xl font-bold tracking-tight text-white text-balance">
            {t("heading")}
          </h1>
          <p className="mt-3 text-sm text-muted max-w-[22rem] m-0 leading-relaxed">
            {t("body")}
          </p>
          <div className="mt-6 flex items-center justify-center gap-3 flex-wrap">
            <Link href="/" className="btn-primary px-5 py-2 text-sm">
              {t("backHome")}
            </Link>
            <Link href="/learn" className="btn-secondary px-5 py-2 text-sm">
              {t("learnCta")}
            </Link>
          </div>
        </div>
      </main>
      <Footer />
    </div>
  );
}
