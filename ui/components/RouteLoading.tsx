import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Card } from "@/components/Card";
import { SkeletonRows } from "@/components/SkeletonRows";

/** I-1-7 / AS-10. Route-level fallback while a data-heavy page's JS or first fetch is in flight. */
export async function RouteLoading({
  mainClassName,
  children,
  footer = true,
}: {
  mainClassName: string;
  /** Replaces the default card of rows when the page's own layout is not a list. */
  children?: ReactNode;
  /** Live game and spectate omit the footer. Replay keeps it. */
  footer?: boolean;
}) {
  const t = await getTranslations("loading");
  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main id="main-content" className={mainClassName}>
        <h1 className="sr-only">{t("heading")}</h1>
        {children ?? (
          <Card>
            <SkeletonRows />
          </Card>
        )}
      </main>
      {footer ? <Footer /> : null}
    </div>
  );
}
