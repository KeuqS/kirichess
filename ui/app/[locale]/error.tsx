"use client";

import { useEffect } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { IconAlertTriangle } from "@tabler/icons-react";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Card } from "@/components/Card";

/**
 * Route-segment error boundary: the layout and stylesheet are fine, only this route threw. Unlike
 * global-error.tsx, this renders inside the normal chrome and uses the token vocabulary.
 *
 * P-7-4: the Sentry client SDK stays out of this module until NEXT_PUBLIC_SENTRY_DSN is set at
 * build time; capture then loads lazily on the first unhandled error.
 */
export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const t = useTranslations("error");

  useEffect(() => {
    const dsn = process.env.NEXT_PUBLIC_SENTRY_DSN;
    if (!dsn) return;
    void import("@sentry/nextjs").then((Sentry) => {
      Sentry.captureException(error);
    });
  }, [error]);

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex-1 flex items-center justify-center px-10 py-24 scroll-mt-24"
      >
        <Card className="page-card">
          <div className="flex flex-col items-center text-center gap-3">
            <IconAlertTriangle size={32} className="text-[var(--loss)]" />
            <h1 className="text-2xl font-bold tracking-tight text-white text-balance">
              {t("heading")}
            </h1>
            <p className="text-sm text-muted max-w-[400px] m-0 leading-relaxed">
              {t("body")}
            </p>
            {/* error.digest is the only handle a user has on a server-side error, and it is the
                same id that appears in the server log. Never render error.message: in production
                Next redacts it to a generic string anyway, and in development the dev overlay is
                the more useful source of truth. */}
            {error.digest && (
              <p className="text-sm text-muted">
                {t("errorId", { digest: error.digest })}
              </p>
            )}
            <div className="flex items-center gap-3 mt-2">
              <button onClick={reset} className="btn-primary px-5 py-2 text-sm">
                {t("tryAgain")}
              </button>
              <Link href="/" className="btn-secondary px-5 py-2 text-sm">
                {t("backHome")}
              </Link>
            </div>
          </div>
        </Card>
      </main>
      <Footer />
    </div>
  );
}
