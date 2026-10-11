// `||`, not `??`: docker-compose.prod.yml passes an explicit empty string when the env var is
// unset (so `docker compose config` still resolves without a .env), and `new URL("")` throws.
export const SITE_URL =
  process.env.NEXT_PUBLIC_SITE_URL || "http://localhost:3000";

export const SITE_NAME = "KiriChess";

export const SITE_DESCRIPTION = "Chess where every move costs, or earns, time.";

/**
 * The project's public existences, as URLs rather than as link copy. Shared because they have
 * two consumers with different jobs: `/about` (and the footer) render them as links a reader
 * clicks, and the root layout's JSON-LD graph publishes them as the `sameAs` claim a crawler
 * reads (M-1-4). A Discord invite in particular is the kind of string that gets regenerated,
 * and two copies of it rot independently.
 *
 * Public development repo (AD.13 / public source of truth). Do not point this at
 * `kirichess-archive` (pre-public private history).
 */
export const GITHUB_URL: string | null =
  "https://github.com/MichielGekiere/kirichess";
export const DISCORD_URL = "https://discord.gg/GRusewDw4";

/**
 * Weekly play hour (ROADMAP 11a.4 / AD.5). One slot, UTC so DST does not move it.
 * Rendered next to the lobby presence readout. English label must match
 * `PresenceIndicator.playHour` in `messages/en.json`.
 */
export const WEEKLY_MEETUP = "Fridays 20:00 UTC";
