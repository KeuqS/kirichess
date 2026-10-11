"use client";

import { useEffect, useState } from "react";
import { usePathname, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconClock } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { Card } from "@/components/Card";
import { EmptyLobbyLeaveBeacon } from "@/components/EmptyLobbyLeaveBeacon";
import { useGameHub } from "@/components/GameHubProvider";
import { markQueued } from "@/lib/emptyLobbyLeave";
import { loginNextHref } from "@/lib/challengeLink";
import { getOpenSeeks } from "@/lib/api";
import { isPresetKey, PRESETS } from "@/lib/presets";
import type { OpenSeekDto } from "@/lib/types";

export const SEEK_POLL_MS = 5_000;

/**
 * Phase 9e.4. MatchQueue already pairs two players in the same pool instantly (RatedPoolsGrid's
 * JoinQueue); the actual gap was visibility - a player arriving a few minutes after someone else
 * queued had no way to know they were there, so both gave up without ever overlapping. This polls
 * the anonymous GET /api/seeks (same reasoning as the live-games list: a stranger deciding whether
 * anyone is home should see one too) and turns each row into a one-click join. There is no separate
 * "accept" call - joining the pool a seek names pairs with it immediately, so this reuses
 * RatedPoolsGrid's own JoinQueue invocation rather than inventing a new endpoint.
 *
 * Renders nothing when the list is empty: an empty seek list is not a feature worth a card, it is
 * just today's normal state on a young site.
 */
export function OpenSeekList() {
  const t = useTranslations("OpenSeekList");
  const router = useRouter();
  const pathname = usePathname();
  const { token } = useAuth();
  const { ensure } = useGameHub();
  const [seeks, setSeeks] = useState<OpenSeekDto[]>([]);
  const [seeksReady, setSeeksReady] = useState(false);
  const [joining, setJoining] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let intervalId: number | null = null;

    async function poll() {
      try {
        const rows = await getOpenSeeks();
        if (!cancelled) {
          setSeeks(rows);
          setSeeksReady(true);
        }
      } catch {
        // A transient failure just leaves the last-known list on screen; the next tick retries.
        // seeksReady stays false until a poll succeeds, so a bounce during load is not an empty lobby.
      }
    }

    function start() {
      void poll();
      if (intervalId == null) {
        intervalId = window.setInterval(() => {
          void poll();
        }, SEEK_POLL_MS);
      }
    }

    function stop() {
      if (intervalId != null) {
        window.clearInterval(intervalId);
        intervalId = null;
      }
    }

    function onVisibility() {
      if (document.hidden) stop();
      else start();
    }

    // I-1-7 / AS-11: a backgrounded lobby tab does not keep hitting GET /api/seeks.
    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      cancelled = true;
      stop();
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, []);

  async function join(pool: string) {
    setError(null);
    // Same honest redirect RatedPoolsGrid uses: the server is the authority either way, this just
    // sends an anonymous visitor somewhere a click can actually do something.
    if (!token) {
      router.push(loginNextHref(pathname));
      return;
    }
    setJoining(pool);
    try {
      const conn = await ensure();
      await conn.invoke("JoinQueue", pool);
      markQueued();
    } catch (err) {
      setError(err instanceof Error ? err.message : t("matchmakingFailed"));
    } finally {
      setJoining(null);
    }
  }

  const list =
    seeks.length === 0 ? null : (
      <Card className="mt-6 flex flex-col gap-3">
        <h2 className="flex items-center gap-2 text-lg font-bold text-white">
          <IconClock size={20} className="text-[var(--bounty)]" />
          {t("openSeeks")}
        </h2>
        <p className="text-sm text-muted">{t("intro")}</p>
        {error && (
          <AlreadyInGameNotice message={error} className="text-sm text-loss" />
        )}
        <ul className="flex flex-col gap-2">
          {seeks.map((seek, i) => {
            const label = isPresetKey(seek.pool)
              ? PRESETS[seek.pool].tag
              : seek.pool;
            return (
              <li
                key={`${seek.pool}-${seek.waitingSinceUtc}-${i}`}
                className="flex items-center justify-between gap-4 border border-[var(--border)] px-4 py-2.5"
              >
                <span className="text-sm text-[var(--foreground)]">
                  {t("waitingLabel", { label })}
                </span>
                <button
                  type="button"
                  onClick={() => join(seek.pool)}
                  disabled={joining === seek.pool}
                  className="btn-primary shrink-0 px-3 py-1.5 text-sm"
                >
                  {joining === seek.pool ? t("joining") : t("join")}
                </button>
              </li>
            );
          })}
        </ul>
      </Card>
    );

  return (
    <>
      <EmptyLobbyLeaveBeacon seekCount={seeks.length} ready={seeksReady} />
      {list}
    </>
  );
}
