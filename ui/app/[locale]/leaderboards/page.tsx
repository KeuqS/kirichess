"use client";

import { useEffect, useState } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { PlayButton } from "@/components/PlayButton";
import { EmptyState } from "@/components/EmptyState";
import { SkeletonRows } from "@/components/SkeletonRows";
import { DataTable } from "@/components/DataTable";
import { Avatar } from "@/components/Avatar";
import { PatronIcon } from "@/components/PatronIcon";
import { getLeaderboard, ApiError } from "@/lib/api";
import { resolveAvatarUrl } from "@/lib/avatarUrl";
import { PRESETS, PRESET_ORDER, type PresetKey } from "@/lib/presets";
import type { LeaderboardEntryDto } from "@/lib/types";

const PAGE_SIZE = 25;
const FETCH_LIMIT = 200; // server clamps to 200; client paginates locally in pages of 25.

const GRID_COLUMNS = "64px 1fr 100px 74px 78px";

export default function Leaderboards() {
  const t = useTranslations("leaderboards");
  const [pool, setPool] = useState<PresetKey>("steal1");
  const [entries, setEntries] = useState<LeaderboardEntryDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setVisibleCount(PAGE_SIZE);

    getLeaderboard(pool, { limit: FETCH_LIMIT })
      .then((rows) => {
        if (cancelled) return;
        setEntries(rows);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(err instanceof ApiError ? err.message : t("failedToLoad"));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [pool, t]);

  const visible = entries.slice(0, visibleCount);
  const hasMore = visibleCount < entries.length;

  const rdExplainer = (
    <div className="text-sm text-muted mb-6 leading-relaxed bg-[var(--surface)] p-3 border border-[var(--border)]">
      {t.rich("rdExplainer", {
        rd: (chunks) => (
          <span className="text-[var(--foreground)]/90 font-semibold">
            {chunks}
          </span>
        ),
        provisional: (chunks) => (
          <span className="text-[var(--bounty)] font-semibold">{chunks}</span>
        ),
      })}
    </div>
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

        {/* POOL TABS */}
        <div className="flex gap-2 mb-6 flex-wrap">
          {PRESET_ORDER.map((key) => {
            const preset = PRESETS[key];
            const active = key === pool;
            return (
              <button
                key={key}
                onClick={() => setPool(key)}
                className={`px-4 py-2 text-sm ${
                  active ? "btn-primary" : "btn-secondary"
                }`}
              >
                {preset.label}
              </button>
            );
          })}
        </div>

        {rdExplainer}

        {loading ? (
          <div className="border border-[var(--border)] bg-[var(--surface)] overflow-x-auto shadow-lg">
            <div
              className="grid gap-2 px-5 py-3 border-b border-[var(--border)] table-header min-w-max"
              style={{ gridTemplateColumns: GRID_COLUMNS }}
            >
              <span className="text-right">{t("colRank")}</span>
              <span>{t("colPlayer")}</span>
              <span className="text-right">{t("colRating")}</span>
              <span className="text-right">{t("colRd")}</span>
              <span className="text-right">{t("colGames")}</span>
            </div>
            <div className="px-5 py-2">
              <SkeletonRows />
            </div>
          </div>
        ) : error ? (
          <div className="border border-[var(--border)] bg-[var(--surface)] py-12 px-8 shadow-lg">
            <EmptyState title={t("loadFailedTitle")} />
          </div>
        ) : entries.length === 0 ? (
          <div className="border border-[var(--border)] bg-[var(--surface)] py-12 px-8 shadow-lg">
            <EmptyState title={t("noRankedTitle")}>
              <PlayButton
                preset={pool}
                className="btn-primary mt-3 px-6 py-3 text-sm"
              >
                {t("playPreset", { preset: PRESETS[pool].label })}
              </PlayButton>
            </EmptyState>
          </div>
        ) : (
          <>
            <DataTable
              gridTemplateColumns={GRID_COLUMNS}
              rows={visible}
              rowKey={(row) => row.userId}
              columns={[
                {
                  header: t("colRank"),
                  align: "right",
                  render: (row) => (
                    <span
                      className={`text-sm font-semibold ${
                        row.rank <= 3 ? "text-[var(--bounty)]" : "text-muted"
                      }`}
                    >
                      {row.rank}
                    </span>
                  ),
                },
                {
                  header: t("colPlayer"),
                  render: (row) => {
                    return (
                      // The row's one link: every name here carries a real user id, and the public
                      // profile it opens is the natural next question after "who is at the top".
                      <Link
                        href={`/u/${row.userId}`}
                        className="flex items-center gap-3 min-w-0 hover:opacity-80 transition-opacity"
                      >
                        <Avatar
                          label={row.displayName.slice(0, 2).toUpperCase()}
                          src={resolveAvatarUrl(row.avatarUrl)}
                          initialsClassName={
                            row.rank <= 3 && !row.avatarUrl
                              ? "text-[var(--bounty)]"
                              : ""
                          }
                        />
                        <span
                          className={`truncate font-semibold ${
                            row.rank <= 3
                              ? "text-[var(--bounty)]"
                              : "text-white"
                          }`}
                        >
                          {row.displayName}
                        </span>
                        <PatronIcon
                          tier={row.patronTier}
                          size={16}
                          className="shrink-0"
                        />
                      </Link>
                    );
                  },
                },
                {
                  header: t("colRating"),
                  align: "right",
                  render: (row) => (
                    <span className="font-bold text-white">
                      {Math.round(row.rating)}
                    </span>
                  ),
                },
                {
                  header: t("colRd"),
                  align: "right",
                  render: (row) => (
                    <span className="text-sm text-muted">
                      {Math.round(row.ratingDeviation)}
                    </span>
                  ),
                },
                {
                  header: t("colGames"),
                  align: "right",
                  render: (row) => (
                    <span className="text-sm text-muted">
                      {row.gamesPlayed}
                    </span>
                  ),
                },
              ]}
            />

            {hasMore && (
              <div className="flex justify-center mt-5">
                <button
                  onClick={() => setVisibleCount((v) => v + PAGE_SIZE)}
                  className="btn-secondary px-5 py-2 text-sm"
                >
                  {t("loadMore", { count: PAGE_SIZE })}
                </button>
              </div>
            )}
          </>
        )}
      </main>
      <Footer />
    </div>
  );
}
