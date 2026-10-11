"use client";

import { useEffect, useState } from "react";
import { usePathname, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { BotGameDialog } from "@/components/BotGameDialog";
import { PresetTile } from "@/components/PresetTile";
import { loginNextHref } from "@/lib/challengeLink";
import { markQueued } from "@/lib/emptyLobbyLeave";
import { getMyRatings } from "@/lib/api";
import { useGameHub } from "@/components/GameHubProvider";
import { isPresetKey, type PresetKey } from "@/lib/presets";

/** P-1-4. Long enough that a real match can still land, short enough that a dead lobby does not look broken. */
export const QUEUE_BOT_NUDGE_MS = 25_000;

const SPEED_ROWS: { id: "bullet" | "blitz" | "rapid"; keys: PresetKey[] }[] = [
  { id: "bullet", keys: ["bounty1", "burn1"] },
  { id: "blitz", keys: ["steal2", "steal1"] },
  { id: "rapid", keys: ["burn2", "steal3"] },
];

/**
 * The home page's rated-pool tiles. Each tile IS a matchmaking queue entry now: there is no
 * instant-play path left on the home page; every tile pairs you with a real, closest-rated
 * opponent online and moves your Glicko-2 rating in that pool. One SignalR connection and one
 * "which pool am I queued for" state are shared across all six tiles here, same lifecycle
 * MatchmakingPanel used to own before this became the primary route to a rated game.
 */
export function RatedPoolsGrid() {
  const t = useTranslations("RatedPoolsGrid");
  const router = useRouter();
  const pathname = usePathname();
  const { token } = useAuth();
  const { ensure } = useGameHub();
  const [searching, setSearching] = useState<PresetKey | null>(null);
  const [offerBot, setOfferBot] = useState(false);
  const [botDialogPool, setBotDialogPool] = useState<PresetKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ratings, setRatings] = useState<Partial<Record<PresetKey, number>>>(
    {},
  );
  const [ratingsLoaded, setRatingsLoaded] = useState(false);
  const speedLabel: Record<"bullet" | "blitz" | "rapid", string> = {
    bullet: t("bulletLabel"),
    blitz: t("blitzLabel"),
    rapid: t("rapidLabel"),
  };

  useEffect(() => {
    if (!token) {
      setRatings({});
      setRatingsLoaded(false);
      return;
    }
    let cancelled = false;
    setRatingsLoaded(false);
    getMyRatings()
      .then((rows) => {
        if (cancelled) return;
        const next: Partial<Record<PresetKey, number>> = {};
        for (const row of rows) {
          if (isPresetKey(row.pool)) next[row.pool] = Math.round(row.rating);
        }
        setRatings(next);
      })
      .catch(() => {
        if (!cancelled) setRatings({});
      })
      .finally(() => {
        if (!cancelled) setRatingsLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  async function toggleQueue(pool: PresetKey) {
    setError(null);
    // Every hub method except JoinAsSpectator is [Authorize]d, so an anonymous JoinQueue throws
    // "Unauthenticated." into the error line below. Sending them to sign in is the honest answer to
    // a tile they can see; the server stays the authority either way.
    if (!token) {
      router.push(loginNextHref(pathname));
      return;
    }
    try {
      const conn = await ensure();
      if (searching === pool) {
        await conn.invoke("LeaveQueue");
        setSearching(null);
      } else {
        if (searching) await conn.invoke("LeaveQueue");
        await conn.invoke("JoinQueue", pool);
        markQueued();
        setSearching(pool);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t("matchmakingFailed"));
      setSearching(null);
    }
  }

  useEffect(() => {
    if (!searching) {
      setOfferBot(false);
      return;
    }
    setOfferBot(false);
    const id = window.setTimeout(() => setOfferBot(true), QUEUE_BOT_NUDGE_MS);
    return () => window.clearTimeout(id);
  }, [searching]);

  async function openBotFallback(pool: PresetKey) {
    setSearching(null);
    setBotDialogPool(pool);
    const conn = await ensure().catch(() => null);
    if (!conn) return;
    try {
      await conn.invoke("LeaveQueue");
    } catch {
      // Best-effort: the bot dialog is the stalled-queue exit even if the hub has dropped.
    }
  }

  return (
    <div className="mb-10 flex flex-col gap-10">
      {error && (
        <AlreadyInGameNotice message={error} className="text-sm text-loss" />
      )}
      {SPEED_ROWS.map((row) => (
        <div key={row.id}>
          <div className="flex items-center gap-4 mb-4">
            <span className="text-sm tracking-widest text-muted font-semibold">
              {speedLabel[row.id]}
            </span>
            <div className="flex-1 h-px bg-[var(--border)]" />
          </div>
          {/* Three columns for two tiles on purpose: a pool tile carries a name, a time
              control, a badge and a rating, and at half of `page-width` (40rem) it is mostly
              empty. Capping each at a third keeps the tile the size of its contents; the
              trailing column stays empty and the speed rule above still spans the page. */}
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-6">
            {row.keys.map((key) => (
              <PresetTile
                key={key}
                presetKey={key}
                rating={ratings[key] ?? null}
                ratingPending={Boolean(token) && !ratingsLoaded}
                searching={searching === key}
                offerBot={searching === key && offerBot}
                onToggle={() => toggleQueue(key)}
                onPlayBot={() => openBotFallback(key)}
              />
            ))}
          </div>
        </div>
      ))}
      {botDialogPool && (
        <BotGameDialog
          presetKey={botDialogPool}
          onClose={() => setBotDialogPool(null)}
        />
      )}
    </div>
  );
}
