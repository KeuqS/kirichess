"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { PlayButton } from "@/components/PlayButton";
import { LiveGameCard } from "@/components/LiveGameCard";
import { EmptyState } from "@/components/EmptyState";
import { Card } from "@/components/Card";
import { SkeletonRows } from "@/components/SkeletonRows";
import { getLiveGames, ApiError } from "@/lib/api";
import { POOL_NAMES } from "@/lib/presets";
import type { LiveGameDto } from "@/lib/types";

const POLL_MS = 5000;

/**
 * How many boards the grid shows. The API already sorts by spectator count then move count, so
 * this is the most-watched games, not an arbitrary slice. Games past it stay watchable by direct
 * link; they are just not advertised here.
 */
const BOARD_COUNT = 12;

/**
 * Clock repaint interval. The play page interpolates at 250ms because it animates a move landing;
 * a lobby has no such moment, so a second is enough and repaints twelve cards a quarter as often.
 */
const TICK_MS = 1000;

const GRID_CLASS = "grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-3";

interface Poll {
  rows: LiveGameDto[];
  /** `Date.now()` when these clocks were read, the anchor the ticker interpolates from. */
  fetchedAt: number;
}

export default function WatchPage() {
  const t = useTranslations("watch");
  const [poll, setPoll] = useState<Poll | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    let cancelled = false;

    function load(isFirst: boolean) {
      getLiveGames(BOARD_COUNT)
        .then((data) => {
          if (cancelled) return;
          setPoll({ rows: data, fetchedAt: Date.now() });
          setError(null);
        })
        .catch((err) => {
          if (cancelled) return;
          // A poll failure keeps the last good rows on screen; only a failed first load renders
          // the error state - a transient blip must not blank the grid out from under a viewer.
          if (isFirst) {
            setError(err instanceof ApiError ? err.message : t("failedToLoad"));
          }
        });
    }

    load(true);
    const interval = setInterval(() => load(false), POLL_MS);

    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [t]);

  // One ticker for the whole grid, not one per card.
  useEffect(() => {
    const interval = setInterval(() => setNow(Date.now()), TICK_MS);
    return () => clearInterval(interval);
  }, []);

  const rows = poll?.rows ?? [];

  /**
   * The running side's clock, counted down from the last poll. Same display-only approximation the
   * server already makes in `GameEndpoints.ApproximateClocks`, and with the same standing: it is
   * never a chargeable time. The owning instance charges think-time off a monotonic anchor, and the
   * next poll corrects whatever this drifted, including a move's economy effect, which can hand
   * time *back* and so is not something a countdown could ever predict.
   */
  function displayTimes(game: LiveGameDto): { white: number; black: number } {
    if (!poll) return { white: game.whiteTime, black: game.blackTime };
    const elapsed = (now - poll.fetchedAt) / 1000;
    return {
      white:
        game.sideToMove === "White"
          ? Math.max(0, game.whiteTime - elapsed)
          : game.whiteTime,
      black:
        game.sideToMove === "Black"
          ? Math.max(0, game.blackTime - elapsed)
          : game.blackTime,
    };
  }

  const grid = (
    <>
      <div className={GRID_CLASS}>
        {rows.map((game) => {
          const { white, black } = displayTimes(game);
          return (
            <LiveGameCard
              key={game.gameId}
              game={game}
              whiteSeconds={white}
              blackSeconds={black}
            />
          );
        })}
      </div>
      {rows.length === BOARD_COUNT && (
        <p className="mt-6 text-center text-sm text-muted leading-relaxed">
          {t("showingMostWatched", { count: BOARD_COUNT })}
        </p>
      )}
    </>
  );

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
      >
        {/* Nav already names the page; keep an h1 for the document outline. */}
        <h1 className="sr-only">{t("pageTitle")}</h1>

        {poll === null && !error ? (
          <Card padding="py-12 px-8" className="shadow-lg">
            <SkeletonRows count={6} />
          </Card>
        ) : error && !poll ? (
          <Card padding="py-12 px-8" className="shadow-lg">
            <EmptyState title={t("loadFailedTitle")} description={error} />
          </Card>
        ) : rows.length === 0 ? (
          <Card padding="py-12 px-8" className="shadow-lg">
            <EmptyState title={t("noGamesTitle")}>
              <PlayButton
                preset="steal1"
                className="btn-primary mt-3 px-6 py-3 text-sm"
              >
                {t("playDefaultPool", POOL_NAMES)}
              </PlayButton>
            </EmptyState>
          </Card>
        ) : (
          grid
        )}
      </main>
      <Footer />
    </div>
  );
}
