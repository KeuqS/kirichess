import type { Metadata } from "next";
import { getLocale } from "next-intl/server";
import { getReplay, ApiError } from "@/lib/api";
import { replayCardText } from "@/lib/replayCard";
import { localizedUrl } from "@/lib/seo";

// page.tsx below is a client component that fetches the replay itself for rendering (loading/error
// states, the interactive board). This layout does its own, separate server-side fetch of the same
// endpoint purely to build metadata/the OG image before the client ever mounts: see
// Phase 9d.6 for why that duplication is the right tradeoff here.
//
// This runs server-side, so it passes API_BASE_INTERNAL (unset outside docker-compose.e2e.yml):
// the browser-facing NEXT_PUBLIC_API_BASE points at a host-published port that isn't reachable
// from inside the frontend container itself.
export async function generateMetadata({
  params,
}: {
  params: Promise<{ id: string }>;
}): Promise<Metadata> {
  const [{ id }, locale] = await Promise.all([params, getLocale()]);
  // M-1-2: a self-referencing canonical, even though robots.ts already disallows `/replay/*` from
  // indexing - an unfurler reads OG tags directly and ignores robots.txt either way, and a
  // canonical costs nothing to add alongside the card this layout already builds.
  const canonical = localizedUrl(locale, `/replay/${id}`);

  try {
    const replay = await getReplay(id, process.env.API_BASE_INTERNAL);
    const { title, description } = replayCardText(replay);
    return {
      title,
      description,
      alternates: { canonical },
      openGraph: { title, description, url: canonical, type: "article" },
      twitter: { title, description },
    };
  } catch (err) {
    // 404 (unfinished/nonexistent game) or the API being briefly unreachable: fall back to a
    // generic card rather than let generateMetadata throw and 500 the page. Metadata merging is
    // shallow per key: a child that omits `openGraph`/`twitter` entirely inherits the *parent's*
    // whole objects, not this fallback `description`, so both need setting explicitly here too.
    const notFound = err instanceof ApiError && err.status === 404;
    const title = "Replay";
    const description = notFound
      ? "This replay isn't available. Games become shareable once they've finished."
      : "Watch a KiriChess replay.";
    return {
      title,
      description,
      alternates: { canonical },
      openGraph: { title, description, url: canonical, type: "article" },
      twitter: { title, description },
    };
  }
}

export default function ReplayLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
