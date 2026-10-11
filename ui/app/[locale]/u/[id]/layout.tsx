import type { Metadata } from "next";
import { getLocale, getTranslations } from "next-intl/server";
import { ApiError, getPublicProfile } from "@/lib/api";
import { routeMetadata } from "@/lib/seo";

// page.tsx below is a client component. This layout fetches the same public profile on the
// server only to set the tab title and share card from the display name (P-1-6), the same
// split replay/[id]/layout.tsx made for OG metadata.
export async function generateMetadata({
  params,
}: {
  params: Promise<{ id: string }>;
}): Promise<Metadata> {
  const [{ id }, locale, t] = await Promise.all([
    params,
    getLocale(),
    getTranslations("publicProfile"),
  ]);
  const path = `/u/${id}`;

  try {
    const profile = await getPublicProfile(id, process.env.API_BASE_INTERNAL);
    return routeMetadata({
      locale,
      path,
      title: profile.displayName,
      description: t("metaDescription", { name: profile.displayName }),
    });
  } catch (err) {
    const notFound = err instanceof ApiError && err.status === 404;
    return routeMetadata({
      locale,
      path,
      title: notFound ? t("notFoundTitle") : t("failedTitle"),
      description: notFound ? t("notFoundDescription") : t("failedToLoad"),
    });
  }
}

export default function PublicProfileLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
