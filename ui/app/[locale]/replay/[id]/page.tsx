"use client";

import { use, useEffect, useRef, useState } from "react";
import dynamic from "next/dynamic";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import {
  IconCheck,
  IconDownload,
  IconLink,
  IconPlayerSkipBack,
  IconPlayerSkipForward,
  IconPlayerTrackNext,
  IconPlayerTrackPrev,
} from "@tabler/icons-react";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Clock } from "@/components/Clock";
import { BoardPageLayout } from "@/components/BoardPageLayout";
import { AccessibleBoard } from "@/components/AccessibleBoard";
import { GapMeter, gapFromClocks } from "@/components/GapMeter";
import { MoveList, type MoveListEntry } from "@/components/MoveList";
import { DataTable } from "@/components/DataTable";
import { PatronIcon } from "@/components/PatronIcon";
import { ClockGraph, type ClockHistoryPoint } from "@/components/ClockGraph";
import {
  analyzeClockSwings,
  CLOCK_CATEGORIES,
  type ClockCategory,
} from "@/lib/clockAnalysis";
import { EmptyState } from "@/components/EmptyState";
import {
  BoardPageSkeleton,
  BoardSkeleton,
} from "@/components/SkeletonRows";
import { Card } from "@/components/Card";
import { ReplayTruncationNotice } from "@/components/ReplayTruncationNotice";
import { PoolBadge } from "@/components/PoolBadge";
import { RuleSummary } from "@/components/RuleSummary";
import { TERMINATION_LABEL_KEY } from "@/components/GameOverOverlay";
import { getReplay, gamePgnUrl, ApiError } from "@/lib/api";
import { PRESETS, isPresetKey, economyOf } from "@/lib/presets";
import { soundForReplayStep } from "@/lib/chessSounds";
import { useChessSounds } from "@/lib/useChessSounds";
import type { ClockEventType, ReplayResponse, Side } from "@/lib/types";

function BoardLoading() {
  const t = useTranslations("replay");
  return <BoardSkeleton label={t("loadingBoard")} />;
}

const Board = dynamic(() => import("@/components/Board").then((m) => m.Board), {
  ssr: false,
  loading: BoardLoading,
});

// `ReplayResponse["result"]` carries the full GameResult enum, including "Ongoing", because the
// generated schema is not endpoint-specific; the route itself (ReplayEndpoints.cs) filters ongoing
// games out with `g.Result != GameResult.Ongoing` before this ever reaches the client, so this case
// is unreachable in practice but still required to keep the map total (Phase 9d.7 cutover).
const RESULT_LABEL_KEY: Record<ReplayResponse["result"], string> = {
  Ongoing: "resultOngoing",
  WhiteWins: "resultWhiteWins",
  BlackWins: "resultBlackWins",
  Draw: "resultDraw",
  Aborted: "resultAborted",
};

const EVENT_LABEL_KEY: Record<ClockEventType, string> = {
  CaptureGain: "eventCaptureGain",
  CaptureLoss: "eventCaptureLoss",
  MovementToll: "eventMovementToll",
  CheckGain: "eventCheckGain",
  CheckLoss: "eventCheckLoss",
  PromotionCost: "eventPromotionCost",
  CastleCost: "eventCastleCost",
  Increment: "eventIncrement",
  RepetitionNullified: "eventRepetitionNullified",
  FlagFall: "eventFlagFall",
};

const CATEGORY_LABEL_KEY: Record<ClockCategory, string> = {
  tolls: "categoryTolls",
  captures: "categoryCaptures",
  checks: "categoryChecks",
  increment: "categoryIncrement",
};

function ReplayContent({ gameId }: { gameId: string }) {
  const t = useTranslations("replay");
  const rulesT = useTranslations("ruleSummary");
  const playSound = useChessSounds();
  const [replay, setReplay] = useState<ReplayResponse | null>(null);
  const [cursor, setCursor] = useState(0); // 0 = start position; n = after ply n
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [clipboardFailed, setClipboardFailed] = useState(false);
  const copyTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    getReplay(gameId)
      .then((data) => {
        if (cancelled) return;
        setReplay(data);
        setCursor(0);
      })
      .catch((err) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 404) {
          setError(t("notAvailable"));
        } else {
          setError(err instanceof Error ? err.message : t("failedToLoad"));
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [gameId]);

  // Derived values: pure, no extra state. The six-field FEN is authoritative for side-to-move, so
  // it is never tracked separately.
  const plies = replay?.plies ?? [];
  const current = cursor > 0 ? plies[cursor - 1] : null;
  const fen = current ? current.fenAfter : (replay?.startFen ?? "");
  const startSeconds = replay?.economy.startTimeSeconds ?? 0;
  const whiteTime = current ? current.whiteTimeAfter : startSeconds;
  const blackTime = current ? current.blackTimeAfter : startSeconds;
  const sideToMove: Side = fen.split(" ")[1] === "b" ? "Black" : "White";

  const clockHistory: ClockHistoryPoint[] = [
    { moveNumber: 0, whiteTime: startSeconds, blackTime: startSeconds },
    ...plies.map((p, i) => ({
      moveNumber: i + 1,
      whiteTime: p.whiteTimeAfter,
      blackTime: p.blackTimeAfter,
    })),
  ];

  const moveEntries: MoveListEntry[] = plies.map((p) => ({
    moveNumber: p.ply,
    side: p.mover,
    uci: p.uci,
    san: p.san,
    whiteDelta: p.whiteDelta,
    blackDelta: p.blackDelta,
  }));

  const analysis = analyzeClockSwings(plies, startSeconds);

  function goTo(n: number) {
    const next = Math.min(Math.max(n, 0), plies.length);
    if (replay && next !== cursor) {
      const cue = soundForReplayStep(replay.startFen, plies, cursor, next);
      if (cue) playSound(cue);
    }
    setCursor(next);
  }

  // Keyboard scrubbing. Every navigation surface (buttons, keys, move-list clicks) routes through
  // the same `goTo` clamp so the cursor can never drift outside [0, plies.length].
  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      switch (event.key) {
        case "ArrowLeft":
          event.preventDefault();
          goTo(cursor - 1);
          break;
        case "ArrowRight":
          event.preventDefault();
          goTo(cursor + 1);
          break;
        case "Home":
          event.preventDefault();
          goTo(0);
          break;
        case "End":
          event.preventDefault();
          goTo(plies.length);
          break;
      }
    }
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [cursor, plies.length]);

  useEffect(() => {
    return () => {
      if (copyTimeoutRef.current) clearTimeout(copyTimeoutRef.current);
    };
  }, []);

  const shareUrl = typeof window === "undefined" ? "" : window.location.href;

  async function handleCopyLink() {
    try {
      await navigator.clipboard.writeText(shareUrl);
      setClipboardFailed(false);
      setCopied(true);
      if (copyTimeoutRef.current) clearTimeout(copyTimeoutRef.current);
      copyTimeoutRef.current = setTimeout(() => setCopied(false), 2000);
    } catch {
      // An insecure context or a denied permission: both real. Degrade to a selectable URL rather
      // than silently doing nothing.
      setClipboardFailed(true);
    }
  }

  if (loading) {
    return (
      <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
        <NavBar />
        <main id="main-content" className="flex-1 page-board py-6 scroll-mt-24">
          <h1 className="sr-only">{t("loadingReplay")}</h1>
          <BoardPageSkeleton />
        </main>
        <Footer />
      </div>
    );
  }

  if (error || !replay) {
    return (
      <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
        <NavBar />
        <main
          id="main-content"
          className="flex-1 page-board flex flex-col items-center py-6 scroll-mt-24"
        >
          <EmptyState
            headingLevel={1}
            title={t("replayUnavailable")}
            description={error ?? t("notAvailable")}
          >
            <Link href="/" className="btn-primary mt-2 px-6 py-3 text-sm">
              {t("backToPlay")}
            </Link>
          </EmptyState>
        </main>
      </div>
    );
  }

  const moverDelta = current
    ? current.mover === "White"
      ? current.whiteDelta
      : current.blackDelta
    : 0;

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex-1 page-board flex flex-col py-6 scroll-mt-24"
      >
        <div className="mb-4">
          <h1 className="flex flex-wrap items-center gap-1.5 text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            <span className="inline-flex items-center gap-1.5">
              {replay.whitePlayerName}
              <PatronIcon tier={replay.whitePatronTier} />
            </span>
            <span className="text-muted">{t("vs")}</span>
            <span className="inline-flex items-center gap-1.5">
              {replay.blackPlayerName}
              <PatronIcon tier={replay.blackPatronTier} />
            </span>
          </h1>
          <p className="flex flex-wrap items-center gap-2 text-sm text-muted">
            <span>{t(RESULT_LABEL_KEY[replay.result])}</span>
            <span aria-hidden="true" className="text-muted">
              -
            </span>
            <span>
              {TERMINATION_LABEL_KEY[replay.termination]
                ? t(TERMINATION_LABEL_KEY[replay.termination])
                : replay.termination}
            </span>
            {!replay.isRated ? (
              <>
                <span aria-hidden="true" className="text-muted">
                  -
                </span>
                <span>{t("unrated")}</span>
              </>
            ) : replay.ratingPool && isPresetKey(replay.ratingPool) ? (
              <>
                <span aria-hidden="true" className="text-muted">
                  -
                </span>
                <PoolBadge economy={economyOf(PRESETS[replay.ratingPool])} />
              </>
            ) : replay.ratingPool ? (
              <>
                <span aria-hidden="true" className="text-muted">
                  -
                </span>
                <span>{replay.ratingPool}</span>
              </>
            ) : null}
            {replay.endedAt && (
              <>
                <span aria-hidden="true" className="text-muted">
                  -
                </span>
                <span>{new Date(replay.endedAt).toLocaleDateString()}</span>
              </>
            )}
          </p>
        </div>

        <ReplayTruncationNotice truncatedAtPly={replay.truncatedAtPly} />

        <BoardPageLayout
          opponentClock={
            <Clock
              variant="compact"
              side="Black"
              seconds={blackTime}
              active={sideToMove === "Black"}
              popups={[]}
            />
          }
          gapMeter={
            <GapMeter gapSeconds={gapFromClocks(whiteTime, blackTime)} />
          }
          localClock={
            <Clock
              variant="compact"
              side="White"
              seconds={whiteTime}
              active={sideToMove === "White"}
              popups={[]}
            />
          }
          board={
            <>
              <Board
                fen={fen}
                legalUci={[]}
                sideToMove={sideToMove}
                onMove={() => {}}
                costFor={() => undefined}
                interactive={false}
                lastMoveUci={current?.uci}
              />

              {/* Read-only accessible view of the same position (9f.2): navigation and reading
                  only, no selection, no move entry, no live announcer. */}
              <AccessibleBoard
                fen={fen}
                legalUci={[]}
                sideToMove={sideToMove}
                you={sideToMove}
                onMove={() => {}}
                costFor={() => undefined}
                interactive={false}
                onAnnounce={() => {}}
              />
            </>
          }
          afterBoard={
            <div className="flex items-center justify-center gap-2">
              <button
                type="button"
                onClick={() => goTo(0)}
                disabled={cursor === 0}
                aria-label={t("firstPosition")}
                className="btn-secondary p-2"
              >
                <IconPlayerSkipBack size={18} />
              </button>
              <button
                type="button"
                onClick={() => goTo(cursor - 1)}
                disabled={cursor === 0}
                aria-label={t("previousMove")}
                className="btn-secondary p-2"
              >
                <IconPlayerTrackPrev size={18} />
              </button>
              <span className="min-w-[64px] text-center text-sm tabular-nums text-muted">
                {cursor} / {plies.length}
              </span>
              <button
                type="button"
                onClick={() => goTo(cursor + 1)}
                disabled={cursor === plies.length}
                aria-label={t("nextMove")}
                className="btn-secondary p-2"
              >
                <IconPlayerTrackNext size={18} />
              </button>
              <button
                type="button"
                onClick={() => goTo(plies.length)}
                disabled={cursor === plies.length}
                aria-label={t("lastPosition")}
                className="btn-secondary p-2"
              >
                <IconPlayerSkipForward size={18} />
              </button>
            </div>
          }
          sidebarClassName="lg:sticky lg:top-[88px] lg:max-h-[calc(100vh-6rem)] lg:overflow-y-auto"
          sidebar={
            <>
            <div className="flex flex-col gap-2">
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={handleCopyLink}
                  className="btn-secondary flex flex-1 gap-2 py-2.5 text-sm"
                >
                  {copied ? <IconCheck size={16} /> : <IconLink size={16} />}
                  {copied ? t("copied") : t("copyLink")}
                </button>
                <a
                  href={gamePgnUrl(gameId)}
                  className="btn-secondary flex flex-1 gap-2 py-2.5 text-sm"
                >
                  <IconDownload size={16} />
                  {t("downloadPgn")}
                </a>
              </div>
              {clipboardFailed && (
                <input
                  readOnly
                  value={shareUrl}
                  onFocus={(e) => e.currentTarget.select()}
                  className="w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
                />
              )}
            </div>
            <Card padding="p-0">
              <div className="border-b border-[var(--border)] px-4 py-2 text-sm font-semibold uppercase tracking-wide text-muted">
                {t("thisMove")}
              </div>
              <div className="px-4 py-3 text-sm">
                {current ? (
                  <>
                    <p className="text-[var(--foreground)]">
                      {t("moveDescription", {
                        ply: current.ply,
                        notation: current.san || current.uci,
                        mover: current.mover,
                      })}
                    </p>
                    <p
                      className={`mt-1 text-2xl font-bold ${
                        moverDelta >= 0
                          ? "text-[var(--gain)]"
                          : "text-[var(--loss)]"
                      }`}
                    >
                      {moverDelta >= 0 ? "+" : ""}
                      {moverDelta}s
                    </p>
                    {current.events.length > 0 && (
                      <ul className="mt-3 flex flex-col gap-1">
                        {current.events.map((ev, i) => (
                          <li
                            key={i}
                            className="flex items-center justify-between gap-2 text-sm"
                          >
                            <span className="text-muted">
                              {t(EVENT_LABEL_KEY[ev.type])} - {ev.side}
                            </span>
                            <span
                              className={
                                ev.amount >= 0
                                  ? "text-[var(--gain)]"
                                  : "text-[var(--loss)]"
                              }
                            >
                              {ev.amount >= 0 ? "+" : ""}
                              {ev.amount}s
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </>
                ) : (
                  <p className="text-muted">{t("startPosition")}</p>
                )}
              </div>
            </Card>

            {!replay.isRated && (
              <Card padding="p-0">
                <div className="border-b border-[var(--border)] px-4 py-2 text-sm font-semibold uppercase tracking-wide text-muted">
                  {rulesT("heading")}
                </div>
                <div className="px-4 py-3">
                  <RuleSummary economy={replay.economy} showTime />
                </div>
              </Card>
            )}

            <MoveList
              moves={moveEntries}
              selectedPly={cursor}
              onSelectPly={goTo}
            />
            </>
          }
        />

        <Card className="mt-8">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">
            {t("clockHistory")}
          </h2>
          <div className="mt-4">
            <DataTable
              gridTemplateColumns="1.4fr 1fr 1fr"
              rowKey={(row) => row.category}
              columns={[
                {
                  header: t("category"),
                  render: (row) => (
                    <span className="font-semibold text-white">
                      {t(CATEGORY_LABEL_KEY[row.category])}
                    </span>
                  ),
                },
                {
                  header: replay.whitePlayerName,
                  align: "right",
                  render: (row) => (
                    <span
                      className={
                        row.white > 0
                          ? "text-[var(--gain)]"
                          : row.white < 0
                            ? "text-[var(--loss)]"
                            : "text-white"
                      }
                    >
                      {row.white >= 0 ? "+" : ""}
                      {row.white}s
                    </span>
                  ),
                },
                {
                  header: replay.blackPlayerName,
                  align: "right",
                  render: (row) => (
                    <span
                      className={
                        row.black > 0
                          ? "text-[var(--gain)]"
                          : row.black < 0
                            ? "text-[var(--loss)]"
                            : "text-white"
                      }
                    >
                      {row.black >= 0 ? "+" : ""}
                      {row.black}s
                    </span>
                  ),
                },
              ]}
              rows={CLOCK_CATEGORIES.map((category) => ({
                category,
                white: analysis.categoryTotals.White[category],
                black: analysis.categoryTotals.Black[category],
              }))}
            />
          </div>
          <div className="mt-5">
            <ClockGraph
              history={clockHistory}
              startTimeSeconds={startSeconds}
            />
          </div>
        </Card>
      </main>
      <Footer />
    </div>
  );
}

export default function ReplayPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);
  return <ReplayContent gameId={id} />;
}
