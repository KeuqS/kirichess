"use client";

import { use, useCallback, useEffect, useRef, useState } from "react";
import dynamic from "next/dynamic";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import * as signalR from "@microsoft/signalr";
import { IconEye } from "@tabler/icons-react";
import { NavBar } from "@/components/NavBar";
import { EmptyState } from "@/components/EmptyState";
import { Card } from "@/components/Card";
import { Clock } from "@/components/Clock";
import { BoardPageLayout } from "@/components/BoardPageLayout";
import { BoardSkeleton } from "@/components/SkeletonRows";
import { BoardLoadingShell } from "@/components/BoardLoadingShell";
import { AccessibleBoard } from "@/components/AccessibleBoard";
import {
  ConnectionStatus,
  type ConnectionState,
} from "@/components/ConnectionStatus";
import { useAnnouncer } from "@/components/LiveAnnouncer";
import { MoveList, type MoveListEntry } from "@/components/MoveList";
import { GapMeter, gapFromClocks } from "@/components/GapMeter";
import { ClockGraph, type ClockHistoryPoint } from "@/components/ClockGraph";
import { PoolBadge } from "@/components/PoolBadge";
import { PatronIcon } from "@/components/PatronIcon";
import { createGameConnection } from "@/lib/realtime";
import { clockSyncFromState, displayClocks, type ClockSync } from "@/lib/liveClock";
import { wakeDisconnectedHub } from "@/lib/hubWake";
import { retryJoin } from "@/lib/retryJoin";
import { economyOf } from "@/lib/presets";
import type {
  GameEndedPayload,
  GameStateResponse,
  MovePlayedDto,
  OpponentDisconnectedPayload,
  SpectatorSnapshot,
  SpectatorsChangedPayload,
  Termination,
} from "@/lib/types";

function BoardLoading() {
  const t = useTranslations("watch");
  return <BoardSkeleton label={t("loadingBoard")} />;
}

const Board = dynamic(() => import("@/components/Board").then((m) => m.Board), {
  ssr: false,
  loading: BoardLoading,
});

// Shared reason codes with GameOverOverlay's TERMINATION_LABEL_KEY, but two of the twelve strings
// are deliberately reworded for a spectator ("a player" rather than "opponent", see the
// OpponentDisconnected handler below), so this file carries its own key set rather than importing.
const TERMINATION_LABEL_KEY: Record<Termination, string> = {
  None: "terminationNone",
  Checkmate: "terminationCheckmate",
  Flagfall: "terminationFlagfall",
  Stalemate: "terminationStalemate",
  Resignation: "terminationResignation",
  DrawByRepetition: "terminationDrawByRepetition",
  DrawByInsufficientMaterial: "terminationDrawByInsufficientMaterial",
  DrawByFiftyMove: "terminationDrawByFiftyMove",
  Abandoned: "terminationAbandoned",
  DrawByAgreement: "terminationDrawByAgreement",
  ServerRestart: "terminationServerRestart",
  NoShow: "terminationNoShow",
};

function WatchContent({ gameId }: { gameId: string }) {
  const t = useTranslations("watch");
  const tConn = useTranslations("ConnectionStatus");
  /**
   * The spectator board is read-only and deliberately has no move announcements (see the
   * AccessibleBoard below), but a dead socket is not board content: it is the reason the clock on
   * screen is lying, and it is the one thing a spectator has to be told out loud.
   */
  const announcer = useAnnouncer();
  const [state, setState] = useState<GameStateResponse | null>(null);
  const [whiteName, setWhiteName] = useState(t("white"));
  const [blackName, setBlackName] = useState(t("black"));
  const [whitePatronTier, setWhitePatronTier] = useState("None");
  const [blackPatronTier, setBlackPatronTier] = useState("None");
  const [moves, setMoves] = useState<MoveListEntry[]>([]);
  const [clockHistory, setClockHistory] = useState<ClockHistoryPoint[]>([]);
  const [spectators, setSpectators] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [notLive, setNotLive] = useState(false);
  const [loading, setLoading] = useState(true);
  const [now, setNow] = useState(0);
  const [presenceNote, setPresenceNote] = useState<string | null>(null);
  /** P-4-1 / P-4-2: the spectator half of the same gap the play page had. */
  const [connState, setConnState] = useState<ConnectionState>("online");

  const stateRef = useRef<GameStateResponse | null>(null);
  const syncRef = useRef<ClockSync | null>(null);

  const resetTicker = useCallback((s: GameStateResponse) => {
    const at = performance.now();
    syncRef.current = clockSyncFromState(s, at);
    setNow(at);
  }, []);

  const applyState = useCallback(
    (s: GameStateResponse) => {
      stateRef.current = s;
      setState(s);
      resetTicker(s);
    },
    [resetTicker],
  );

  // Connect and join as a spectator. One connection for the life of the page. There is no
  // LeaveSpectate: stopping the connection on unmount is the only exit path (README § contract).
  useEffect(() => {
    let cancelled = false;
    const conn = createGameConnection();

    conn.on("MovePlayed", (p: MovePlayedDto) => {
      const prior = stateRef.current?.sideToMove ?? p.state.sideToMove;
      setMoves((prev) => [
        ...prev,
        {
          moveNumber: prev.length + 1,
          side: prior,
          uci: p.uci,
          whiteDelta: p.whiteDelta,
          blackDelta: p.blackDelta,
        },
      ]);
      setClockHistory((prev) => [
        ...prev,
        {
          moveNumber: prev.length,
          whiteTime: p.state.whiteTime,
          blackTime: p.state.blackTime,
        },
      ]);
      applyState(p.state);
    });

    conn.on("GameEnded", (p: GameEndedPayload) => {
      setPresenceNote(null);
      applyState(p.state);
    });

    conn.on("SpectatorsChanged", (p: SpectatorsChangedPayload) =>
      setSpectators(p.count),
    );

    // Shared with players (README § contract), worded for a spectator: "a player", not "opponent".
    conn.on("OpponentDisconnected", (p: OpponentDisconnectedPayload) =>
      setPresenceNote(t("playerDisconnected", { seconds: p.graceSeconds })),
    );
    conn.on("OpponentReconnected", () => setPresenceNote(null));

    const join = async () => {
      const snap = (await conn.invoke(
        "JoinAsSpectator",
        gameId,
      )) as SpectatorSnapshot;
      if (cancelled) return;

      setWhiteName(snap.whiteName);
      setBlackName(snap.blackName);
      setWhitePatronTier(snap.whitePatronTier);
      setBlackPatronTier(snap.blackPatronTier);
      setSpectators(snap.spectators);
      setMoves(
        snap.moves.map((m) => ({
          moveNumber: m.ply,
          side: m.mover,
          uci: m.uci,
          whiteDelta: m.whiteDelta,
          blackDelta: m.blackDelta,
        })),
      );
      const startSeconds =
        snap.state.economy?.startTimeSeconds ?? snap.state.whiteTime;
      setClockHistory([
        { moveNumber: 0, whiteTime: startSeconds, blackTime: startSeconds },
        ...snap.moves.map((m, i) => ({
          moveNumber: i + 1,
          whiteTime: m.whiteTimeAfter,
          blackTime: m.blackTimeAfter,
        })),
      ]);
      applyState(snap.state);
    };

    // P-4-1. Identical to the play page's handling, because the symptom is identical: the cosmetic
    // ticker below keeps counting a clock the server is no longer confirming.
    conn.onreconnecting(() => {
      setConnState("reconnecting");
      announcer.assertive(tConn("reconnecting"));
    });

    conn.onreconnected(() => {
      // P-4-7. Same retry as the play page: JoinAsSpectator is a no-op that returns a fresh
      // snapshot, so retrying is safe. Stay on reconnecting until it actually succeeds.
      void retryJoin(join, { isCancelled: () => cancelled }).then((result) => {
        if (cancelled) return;
        if (result === "ok") {
          setConnState("online");
          announcer.assertive(tConn("restored"));
          return;
        }
        if (result === "failed") {
          setConnState("resyncFailed");
          announcer.assertive(tConn("resyncFailed"));
        }
      });
    });

    // P-4-2. End of P-6-5's reconnect schedule, after which no MovePlayed or
    // GameEnded can arrive. `cancelled` distinguishes it from the `conn.stop()`
    // in the cleanup below.
    conn.onclose(() => {
      if (cancelled) return;
      setConnState("closed");
      announcer.assertive(
        tConn("closedAnnouncementTemplate", {
          title: tConn("closedTitle"),
          body: tConn("closedBody"),
        }),
      );
    });

    // P-6-5. Same wake as the play page: if iOS exhausted the retry schedule
    // while the tab was backgrounded, start() and JoinAsSpectator again.
    let waking = false;
    const onVisibility = () => {
      if (cancelled || waking) return;
      if (document.visibilityState !== "visible") return;
      if (conn.state !== signalR.HubConnectionState.Disconnected) return;
      waking = true;
      setConnState("reconnecting");
      void wakeDisconnectedHub({
        visibilityState: document.visibilityState,
        hubState: conn.state,
        start: () => conn.start(),
      }).then(async (wake) => {
        if (cancelled) return;
        if (wake === "skipped") {
          waking = false;
          return;
        }
        if (wake === "failed") {
          waking = false;
          setConnState("closed");
          return;
        }
        const result = await retryJoin(join, { isCancelled: () => cancelled });
        waking = false;
        if (cancelled) return;
        if (result === "ok") {
          setConnState("online");
          announcer.assertive(tConn("restored"));
          return;
        }
        if (result === "failed") {
          setConnState("resyncFailed");
          announcer.assertive(tConn("resyncFailed"));
        }
      });
    };
    document.addEventListener("visibilitychange", onVisibility);

    (async () => {
      try {
        await conn.start();
        await join();
      } catch (err) {
        if (!cancelled) {
          const message = err instanceof Error ? err.message : "";
          // Matches the server's literal error text, not user-visible copy: left untranslated on
          // purpose, since translating it would break the detection.
          if (message.includes("Game not found")) {
            setNotLive(true);
          } else if (message.includes("Busy right now")) {
            // 10a.2's connection budget, refused at negotiate so start() rejects rather than the
            // socket dying quietly. Same detect-then-translate shape as the line above.
            setError(t("serverBusy"));
          } else {
            setError(message || t("failedToJoinSpectator"));
          }
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisibility);
      conn.stop();
    };
  }, [gameId, applyState, t, tConn, announcer.assertive]);

  // Clock tick, same interpolation as the play page. P-4-8: freeze while the hub is down so a
  // ticking spectator clock is not a false live signal.
  useEffect(() => {
    if (connState !== "online") return;
    setNow(performance.now());
    const interval = setInterval(() => setNow(performance.now()), 100);
    return () => clearInterval(interval);
  }, [connState]);

  function displayTimes(): { white: number; black: number } {
    if (!state) return { white: 0, black: 0 };
    if (state.status !== "Ongoing") {
      return { white: state.whiteTime, black: state.blackTime };
    }
    const sync = syncRef.current;
    if (!sync) return { white: state.whiteTime, black: state.blackTime };
    return displayClocks(sync, now, true);
  }

  if (loading) {
    return (
      <BoardLoadingShell label={t("pageTitle")} status={t("loadingGame")} />
    );
  }

  if (notLive) {
    return (
      <div className="board-page-safe-bottom min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
        <NavBar />
        <main
          id="main-content"
          className="page-board flex flex-col items-center py-6 scroll-mt-24"
        >
          <EmptyState headingLevel={1} title={t("noLongerLive")}>
            <Link href="/watch" className="btn-primary mt-2 px-6 py-3 text-sm">
              {t("backToWatch")}
            </Link>
          </EmptyState>
        </main>
      </div>
    );
  }

  if (!state) {
    return (
      <div className="board-page-safe-bottom min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
        <NavBar />
        <main
          id="main-content"
          className="page-board flex flex-col items-center py-6 scroll-mt-24"
        >
          <EmptyState
            headingLevel={1}
            title={t("gameNotFound")}
            description={error ?? undefined}
          >
            <Link href="/watch" className="btn-primary mt-2 px-6 py-3 text-sm">
              {t("backToWatch")}
            </Link>
          </EmptyState>
        </main>
      </div>
    );
  }

  const { white, black } = displayTimes();
  const gameOver = state.status !== "Ongoing";
  const connected = connState === "online";
  const resultText = state.winner
    ? t("winnerWins", { winner: state.winner })
    : t("drawResult");
  const economyBadge = state.economy
    ? economyOf({
        captorGain: state.economy.captureCaptorGainEnabled,
        victimLoss: state.economy.captureVictimLossEnabled,
      })
    : null;

  return (
    <div className="board-page-safe-bottom min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      {announcer.node}
      <main
        id="main-content"
        className="page-board flex flex-col py-6 scroll-mt-24"
      >
        <h1 className="sr-only">{t("pageTitle")}</h1>
        {error && <p className="mb-4 text-sm text-[var(--loss)]">{error}</p>}
        <ConnectionStatus state={connState} />
        {presenceNote && <p className="mb-4 notice-banner">{presenceNote}</p>}
        {gameOver && (
          <Card padding="px-4 py-3" className="mb-4 text-center">
            <span className="font-bold text-[var(--bounty)]">
              {TERMINATION_LABEL_KEY[state.termination]
                ? t(TERMINATION_LABEL_KEY[state.termination])
                : state.termination}
            </span>
            <span className="ml-2 text-white">{resultText}</span>
          </Card>
        )}

        <BoardPageLayout
          opponentHeader={
            <div className="flex items-center justify-center gap-1.5 text-center text-sm font-semibold text-muted">
              {blackName}
              <PatronIcon
                tier={blackPatronTier}
                size={14}
                className="shrink-0"
              />
            </div>
          }
          opponentClock={
            <Clock
              variant="compact"
              side="Black"
              seconds={black}
              active={state.sideToMove === "Black" && !gameOver && connected}
              frozen={!connected}
              popups={[]}
            />
          }
          gapMeter={<GapMeter gapSeconds={gapFromClocks(white, black)} />}
          localHeader={
            <div className="flex items-center justify-center gap-1.5 text-center text-sm font-semibold text-muted">
              {whiteName}
              <PatronIcon
                tier={whitePatronTier}
                size={14}
                className="shrink-0"
              />
            </div>
          }
          localClock={
            <Clock
              variant="compact"
              side="White"
              seconds={white}
              active={state.sideToMove === "White" && !gameOver && connected}
              frozen={!connected}
              popups={[]}
            />
          }
          board={
            <>
              <Board
                fen={state.fen}
                legalUci={[]}
                sideToMove={state.sideToMove}
                onMove={() => {}}
                costFor={() => undefined}
                lastMoveUci={moves.at(-1)?.uci}
              />

              {/* Read-only accessible view of the same position (9f.2): navigation and reading
                  only, no selection, no move entry, no live announcer. */}
              <AccessibleBoard
                fen={state.fen}
                legalUci={[]}
                sideToMove={state.sideToMove}
                you={state.sideToMove}
                onMove={() => {}}
                costFor={() => undefined}
                interactive={false}
                onAnnounce={() => {}}
              />
            </>
          }
          sidebar={
            <>
              <Card padding="p-0">
                <div className="flex items-center gap-2 border-b border-[var(--border)] px-4 py-2 text-sm font-semibold uppercase tracking-wide text-muted">
                  <IconEye size={14} stroke={2} />
                  {t("watchingCount", { count: spectators })}
                </div>
                <div className="flex flex-col gap-2 px-4 py-3">
                  {economyBadge && <PoolBadge economy={economyBadge} />}
                  <div className="flex flex-wrap items-center gap-1 text-sm text-white">
                    {whiteName}
                    <PatronIcon
                      tier={whitePatronTier}
                      size={14}
                      className="shrink-0"
                    />
                    <span className="text-muted">{t("vs")}</span>
                    {blackName}
                    <PatronIcon
                      tier={blackPatronTier}
                      size={14}
                      className="shrink-0"
                    />
                  </div>
                </div>
              </Card>

              <MoveList moves={moves} />
            </>
          }
        />

        {gameOver && (
          <div className="mt-6 flex justify-center">
            <ClockGraph
              history={clockHistory}
              startTimeSeconds={state.economy?.startTimeSeconds ?? 300}
            />
          </div>
        )}
      </main>
    </div>
  );
}

export default function WatchGamePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);
  return <WatchContent gameId={id} />;
}
