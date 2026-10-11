"use client";

import { useEffect, type ReactNode } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { SkeletonRows } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import { ForbiddenScene } from "@/components/ForbiddenScene";

/**
 * Player-facing 403, the pair of the custom 404. Staff dashboards stay English
 * (audience of one); this screen is what everyone else sees, so it lives in the
 * catalogue.
 */
export function ForbiddenScreen() {
  const t = useTranslations("forbidden");

  useEffect(() => {
    const previousTitle = document.title;
    document.title = `${t("title")} - KiriChess`;
    return () => {
      document.title = previousTitle;
    };
  }, [t]);

  return (
    <main
      id="main-content"
      className="flex-1 flex items-center justify-center px-6 py-16 sm:px-10 sm:py-24 scroll-mt-24"
    >
      <div className="page-card flex flex-col items-center text-center">
        <ForbiddenScene />
        <p className="mt-4 m-0 text-2xl font-bold tracking-tight text-[var(--bounty)]">
          {t("code")}
        </p>
        <h1 className="mt-2 m-0 font-bold text-2xl tracking-tight text-white text-balance">
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
  );
}

/**
 * Staff page body. Guests and ordinary players get the 403 screen; admins get
 * `children`. `isAdmin` is a UI affordance, not a security boundary - every
 * staff route is still enforced server-side.
 */
export function StaffOnly({ children }: { children: ReactNode }) {
  const { isAdmin, hydrated } = useAuth();
  const t = useTranslations("RequireAuth");

  if (!hydrated) {
    return (
      <main
        id="main-content"
        className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
      >
        <h1 className="sr-only">{t("loading")}</h1>
        <SkeletonRows count={6} />
      </main>
    );
  }

  if (!isAdmin) return <ForbiddenScreen />;
  return <>{children}</>;
}
