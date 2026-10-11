"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { SkeletonStatus } from "@/components/SkeletonRows";

export function RequireAuth({
  children,
  fallback,
}: {
  children: React.ReactNode;
  /** Replaces the generic bars when the gated page already has a layout to hold. */
  fallback?: React.ReactNode;
}) {
  const t = useTranslations("RequireAuth");
  const { token, hydrated } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (hydrated && !token) {
      // Carry where they were trying to go, query string included, so a link to a gated page
      // survives the login detour. Dropping `?preset=` or `?accept=` would land them on the page
      // without the mode the link was for.
      //
      // Read the query string from `window.location` inside the effect rather than via
      // `useSearchParams`, which would oblige every gated page in the app to grow a Suspense
      // boundary. The effect only ever runs in the browser, so there is no server render to
      // disagree with. `/login` sanitises the value through `safeNextPath` before acting on it.
      //
      // The path, though, comes from next-intl's `usePathname`, which strips the locale prefix -
      // `window.location.pathname` would read "/nl/profile", and `router.replace` puts the prefix
      // back on, so a Dutch reader would be sent to "/nl/nl/profile" and 404.
      const here = pathname + window.location.search;
      router.replace(
        here === "/" ? "/login" : `/login?next=${encodeURIComponent(here)}`,
      );
    }
  }, [hydrated, token, router, pathname]);

  if (!hydrated || !token) {
    if (fallback) return fallback;
    return (
      <div className="flex min-h-[50vh] items-center justify-center px-6">
        <div className="w-full max-w-md">
          <SkeletonStatus label={t("loading")} count={4} />
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
