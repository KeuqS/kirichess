import type { Metadata } from "next";
import React from "react";
import { getLocale, getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { DonationTiers } from "@/components/DonationTiers";
import { routeMetadata } from "@/lib/seo";

// M-1-1 / M-1-2: see about/page.tsx's comment - same fix, same reason.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("donate"),
  ]);
  return routeMetadata({
    locale,
    path: "/donate",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

export default async function Donate() {
  const t = await getTranslations("donate");
  return (
    <div className="min-h-screen bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34] flex flex-col">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-width flex flex-col justify-center pt-10 pb-24 scroll-mt-24"
      >
        <h1 className="sr-only">{t("heading")}</h1>

        <DonationTiers />

        <section className="mt-16 pt-16 border-t border-[var(--border)]">
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-12">
            {t("intro")}
          </p>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-12">
            <div>
              <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
                {t("whereMoneyGoesQ")}
              </h2>
              <p className="text-sm text-[var(--foreground)] leading-relaxed">
                {t("whereMoneyGoesA")}
              </p>
            </div>
            <div>
              <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
                {t("patronFeaturesQ")}
              </h2>
              <p className="text-sm text-[var(--foreground)] leading-relaxed">
                {t("entirelyFree")}
                <br />
                <br />
                {t("patronBadgeNote")}
              </p>
            </div>
          </div>
        </section>
      </main>

      <Footer />
    </div>
  );
}
