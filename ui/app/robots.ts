import type { MetadataRoute } from "next";
import { SITE_URL } from "@/lib/site";

// Disallow list is split into two different reasons, not one "gated pages" bucket:
//  - /moderation, /analytics: non-admins (including crawlers) see a 403 screen, not the
//    dashboard, so indexing them is pointless at best.
//  - /profile, /custom: RequireAuth-gated. An anonymous crawler sees a login wall, not the
//    real content, so indexing them is pointless at best.
//  - /replay/*, /watch/*, /game/*: real, anonymously-viewable content, but deliberately excluded
//    from search indexing anyway: thin, one-off, near-duplicate pages by the thousand would dilute
//    the site's overall ranking for no benefit (nobody searches Google for a specific past game).
//    This is independent of openGraph/Twitter card support for /replay/[id]: chat-app link
//    unfurlers (Discord etc.) fetch a URL's OG tags directly and don't consult robots.txt, so a
//    disallowed replay link still pastes as a rich card, see Phase 9d.6
//  - /reset-password, /verify-email, /auth/callback, /unsubscribe: carry a one-time email/OAuth
//    token as a query param. A crawled/cached copy of that URL is a leaked credential (for
//    /unsubscribe, the "credential" fires on load: unsubscribeFromNotifications runs as soon as
//    the page mounts, so a bot merely fetching the URL flips the user's preference), not just
//    low-value content. M-1-3.
//  - /offline: the service worker's offline fallback shell (9f.3), not a page anyone navigates to
//    on purpose.
//  - /u/*: public profiles. Anonymously viewable by design, and excluded here for the same
//    thin-page reason as /replay/* plus one more: indexing a page per account publishes every
//    display name to search engines, which is a call for the deployment owner to make on purpose
//    rather than something a new route should do by default. Removing this line is all it takes.
//  - /challenge/: a live invite token in the path, same shape as /replay/* and /watch/*. The rich
//    OG/Twitter card is still intentional (Phase 9d.8) - unfurlers don't consult robots.txt, so a
//    disallowed challenge link still pastes as a card - this line only keeps it out of the index.
export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: "*",
      allow: "/",
      disallow: [
        "/moderation",
        "/analytics",
        "/profile",
        "/custom",
        "/replay/",
        "/watch/",
        "/game/",
        "/reset-password",
        "/verify-email",
        "/auth/callback",
        "/unsubscribe",
        "/offline",
        "/u/",
        "/challenge/",
      ],
    },
    sitemap: `${SITE_URL}/sitemap.xml`,
  };
}
