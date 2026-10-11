"use client";

import { useCallback, useEffect, useState } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { Avatar } from "@/components/Avatar";
import { DataTable } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { SkeletonRows } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import { ApiError, getMyGames } from "@/lib/api";
import { PRESETS, type PresetKey } from "@/lib/presets";
import type { GameHistoryEntryDto } from "@/lib/types";

const PAGE_SIZE = 15;
const GRID_COLUMNS = "1fr 90px 110px 90px 100px";

type Translator = (
  key: string,
  values?: Record<string, string | number>,
) => string;

function outcome(
  row: GameHistoryEntryDto,
  t: Translator,
): { label: string; colorVar: string } {
  if (row.result === "Draw")
    return { label: t("draw"), colorVar: "--foreground" };
  if (row.result === "Aborted")
    return { label: t("aborted"), colorVar: "--foreground" };
  const won =
    (row.result === "WhiteWins" && row.mySide === "White") ||
    (row.result === "BlackWins" && row.mySide === "Black");
  return won
    ? { label: t("win"), colorVar: "--gain" }
    : { label: t("loss"), colorVar: "--loss" };
}

function poolLabel(pool: string | null, t: Translator): string {
  if (pool === null) return t("customLabel");
  return pool in PRESETS ? PRESETS[pool as PresetKey].label : pool;
}

function formatDate(iso: string | null): string {
  if (!iso) return "-";
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

/**
 * Phase 9e.1's "MATCH HISTORY: COMING SOON" replacement. Renders nothing without a token, like
 * DisplayNameCard/SecurityCard/AccountDataCard on the same profile page. Paginates with a
 * keyset cursor on the last row (I-1-4), not a client-side slice of one big fetch and not
 * an offset: a player's game count is unbounded, unlike a leaderboard capped at 200.
 */
export function MatchHistoryCard() {
  const t = useTranslations("MatchHistoryCard");
  const { token } = useAuth();
  const [rows, setRows] = useState<GameHistoryEntryDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    setLoading(true);
    setError(null);

    getMyGames({ take: PAGE_SIZE })
      .then((data) => {
        if (cancelled) return;
        setRows(data);
        setHasMore(data.length === PAGE_SIZE);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof ApiError ? err.message : t("failedToLoadHistory"),
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [token, t]);

  const loadMore = useCallback(() => {
    const last = rows[rows.length - 1];
    if (!last?.endedAt) return;
    setLoadingMore(true);
    getMyGames({ take: PAGE_SIZE, before: last.endedAt, beforeId: last.gameId })
      .then((data) => {
        setRows((prev) => [...prev, ...data]);
        setHasMore(data.length === PAGE_SIZE);
      })
      .catch((err) => {
        setError(err instanceof ApiError ? err.message : t("failedToLoadMore"));
      })
      .finally(() => setLoadingMore(false));
  }, [rows, t]);

  if (!token) return null;

  return (
    <div className="mb-8">
      <h2 className="profile-category mb-4">{t("matchHistory")}</h2>

      {loading ? (
        <div className="border border-[var(--border)] bg-[var(--surface)] overflow-x-auto shadow-lg">
          <div className="px-5 py-4">
            <SkeletonRows />
          </div>
        </div>
      ) : error ? (
        <div className="border border-[var(--border)] bg-[var(--surface)] py-12 px-8 shadow-lg">
          <EmptyState title={t("loadFailedTitle")} description={error} />
        </div>
      ) : rows.length === 0 ? (
        <div className="border border-dashed border-[var(--border)] bg-[var(--surface)] p-10 shadow-lg">
          <EmptyState
            title={t("noGamesTitle")}
            description={t("noGamesDescription")}
          />
        </div>
      ) : (
        <>
          <DataTable
            gridTemplateColumns={GRID_COLUMNS}
            rows={rows}
            rowKey={(row) => row.gameId}
            columns={[
              {
                header: t("colOpponent"),
                render: (row) => (
                  <Link
                    href={`/replay/${row.gameId}`}
                    className="flex items-center gap-3 hover:opacity-80 transition-opacity"
                  >
                    <Avatar
                      label={row.opponentName.slice(0, 2).toUpperCase()}
                    />
                    <span className="font-semibold text-white">
                      {row.opponentName}
                    </span>
                  </Link>
                ),
              },
              {
                header: t("colResult"),
                render: (row) => {
                  const { label, colorVar } = outcome(row, t);
                  return (
                    <span
                      className="font-bold text-sm"
                      style={{ color: `var(${colorVar})` }}
                    >
                      {label}
                    </span>
                  );
                },
              },
              {
                header: t("colPool"),
                render: (row) => (
                  <span className="text-sm text-muted">
                    {poolLabel(row.ratingPool, t)}
                  </span>
                ),
              },
              {
                header: t("colRating"),
                align: "right",
                render: (row) => {
                  if (row.ratingBefore === null || row.ratingAfter === null) {
                    return <span className="text-sm text-muted">-</span>;
                  }
                  const delta = row.ratingAfter - row.ratingBefore;
                  const sign = delta > 0 ? "+" : "";
                  return (
                    <span
                      className={`text-sm font-semibold ${
                        delta > 0
                          ? "text-[var(--gain)]"
                          : delta < 0
                            ? "text-[var(--loss)]"
                            : "text-muted"
                      }`}
                    >
                      {sign}
                      {Math.round(delta)}
                    </span>
                  );
                },
              },
              {
                header: t("colDate"),
                align: "right",
                render: (row) => (
                  <span className="text-sm text-muted">
                    {formatDate(row.endedAt)}
                  </span>
                ),
              },
            ]}
          />

          {loadingMore && (
            <div role="status" className="px-5">
              <span className="sr-only">{t("loadingEllipsis")}</span>
              <SkeletonRows count={3} />
            </div>
          )}

          {hasMore && (
            <div className="flex justify-center mt-5">
              <button
                onClick={loadMore}
                disabled={loadingMore}
                className="btn-secondary px-5 py-2 text-sm"
              >
                {loadingMore ? t("loadingEllipsis") : t("loadMore")}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
