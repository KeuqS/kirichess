import type { Metadata } from "next";
import { getLocale } from "next-intl/server";
import { getChallenge, ApiError } from "@/lib/api";
import { PRESETS, isPresetKey } from "@/lib/presets";
import { localizedUrl } from "@/lib/seo";

// page.tsx below is a client component that fetches the challenge itself for rendering (loading,
// the accept control, auto-accept). This layout does its own separate server-side fetch of the
// same endpoint purely to build metadata before the client ever mounts, the same tradeoff
// replay/[id]/layout.tsx made in Phase 9d.6 (see Phase 9d.6), and for the same
// reason: this URL's entire job (Phase 9d.8 step 5) is to be pasted
// into a chat window, and the preview card is what a friend sees before they ever load the page.
export async function generateMetadata({
  params,
}: {
  params: Promise<{ token: string }>;
}): Promise<Metadata> {
  const [{ token }, locale] = await Promise.all([params, getLocale()]);
  // M-1-2: same reasoning as replay/[id]/layout.tsx - robots.ts disallows `/challenge/*` (M-1-3),
  // but the canonical costs nothing to add alongside the card this layout already builds.
  const canonical = localizedUrl(locale, `/challenge/${token}`);

  try {
    const challenge = await getChallenge(token);
    const poolLabel = isPresetKey(challenge.pool)
      ? PRESETS[challenge.pool].tag
      : challenge.pool === "custom"
        ? "Custom game"
        : challenge.pool;
    const title = `${challenge.challengerName} challenged you`;
    const description = `${poolLabel} - Unrated. Play them directly on KiriChess, no queue, no waiting for a stranger.`;
    return {
      title,
      description,
      alternates: { canonical },
      openGraph: { title, description, url: canonical, type: "article" },
      twitter: { title, description },
    };
  } catch (err) {
    // 404 (unknown/expired token) or the API being briefly unreachable: fall back to a generic
    // card rather than let generateMetadata throw and 500 the page. Metadata merging is shallow
    // per key, a child that omits openGraph/twitter entirely inherits the *parent's* whole
    // objects, not this fallback description, so both need setting explicitly here too.
    const notFound = err instanceof ApiError && err.status === 404;
    const title = notFound ? "Challenge not found" : "Play a friend";
    const description = notFound
      ? "That challenge link is not valid."
      : "Someone challenged you to a game on KiriChess.";
    return {
      title,
      description,
      alternates: { canonical },
      openGraph: { title, description, url: canonical, type: "article" },
      twitter: { title, description },
    };
  }
}

export default function ChallengeLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
