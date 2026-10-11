"use client";

import { use, useCallback, useEffect, useRef, useState } from "react";
import dynamic from "next/dynamic";
import { useSearchParams } from "next/navigation";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import * as signalR from "@microsoft/signalr";
import { RequireAuth } from "@/components/RequireAuth";
import { useAuth } from "@/components/AuthProvider";
import { NavBar } from "@/components/NavBar";
import { EmptyState } from "@/components/EmptyState";
import { Card } from "@/components/Card";
import { Clock } from "@/components/Clock";
import { BoardPageLayout } from "@/components/BoardPageLayout";
import { BoardSkeleton } from "@/components/SkeletonRows";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { BoardLoadingShell } from "@/components/BoardLoadingShell";
import { MoveList, type MoveListEntry } from "@/components/MoveList";
import {
  GameOverOverlay,
  TERMINATION_LABEL_KEY,
  formatRatingDelta,
} from "@/components/GameOverOverlay";
import { GapMeter, gapFromClocks } from "@/components/GapMeter";
import { CapturedMaterialPanel } from "@/components/CapturedMaterialPanel";
import { GameControls } from "@/components/GameControls";
import {
  ConnectionStatus,
  type ConnectionState,
} from "@/components/ConnectionStatus";
import { AccessibleBoard } from "@/components/AccessibleBoard";
import { useAnnouncer } from "@/components/LiveAnnouncer";
import type { ClockHistoryPoint } from "@/components/ClockGraph";
import type { TimeDeltaPopup } from "@/components/TimeDeltaLayer";
import { createGameConnection, stopWhenSettled } from "@/lib/realtime";
import { wakeDisconnectedHub } from "@/lib/hubWake";
import { retryJoin } from "@/lib/retryJoin";
import { localPlayerSide } from "@/lib/gameSide";
import { historyFromSnapshot } from "@/lib/gameHistory";
import { rematchUiState } from "@/lib/rematchUiState";
import { computeCostPreview, economyFromPreset } from "@/lib/costPreview";
import { crossedThreshold, describeClocks, describeMove } from "@/lib/a11y";
import { clockSyncFromState, displayClocks, type ClockSync } from "@/lib/liveClock";
import { PRESETS, isPresetKey, type PresetKey } from "@/lib/presets";
import type {
  ClockEventDto,
  GameSnapshot,
  GameStateResponse,
  MovePlayedDto,
  GameEndedPayload,
  GameRatingChange,
  RematchAcceptedPayload,
  DrawOfferedPayload,
  OpponentDisconnectedPayload,
  Side,
} from "@/lib/types";
import {
  createBotEngine,
  getRandomMove,
  isBotSearchAbandoned,
  type BotEngine,
} from "@/lib/bot";
import {
  createThinkDelay,
  remainingThinkMsAfterWake,
  simulatedThinkMs,
} from "@/lib/botClock";
import { rememberBotGame } from "@/lib/returnToGame";
import { soundForMove } from "@/lib/chessSounds";
import { isCapture, capturedPieceType, type PieceChar } from "@/lib/fen";
import { useChessSounds } from "@/lib/useChessSounds";

function BoardLoading() {
  const t = useTranslations("game");
  return <BoardSkeleton label={t("loadingBoard")} />;
}

const Board = dynamic(() => import("@/components/Board").then((m) => m.Board), {
  ssr: false,
  loading: BoardLoading,
});

type Translator = (
  key: string,
  values?: Record<string, string | number>,
) => string;

/**
 * Same wording `GameOverOverlay` shows visually, so the assertive announcement and the overlay
 * never disagree (Step 4 of the 9f.2 plan). Takes the whole state rather than just `termination`/
 * `winner` so it reads the same as the overlay's own two-field derivation. `t` is this page's own
 * translator (its own "game" namespace, matching GameOverOverlay's separate copy of the same
 * termination labels) rather than something threaded from further up.
 *
 * `myRatingChange` folds the Glicko-2 delta (Phase 11a.2) into this same utterance rather than a
 * second announcement fired after it (9f.2's accessible layer builds one utterance per event), null
 * for an unrated game or before the payload carrying it has arrived.
 */
function resultAnnouncement(
  state: GameStateResponse,
  t: Translator,
  myRatingChange: { before: number; after: number } | null,
): string {
  const key = TERMINATION_LABEL_KEY[state.termination];
  const label = key ? t(key) : state.termination;
  const resultText = state.winner
    ? t("winnerWins", { winner: state.winner })
    : state.termination === "ServerRestart"
      ? t("gameAbortedNoRating")
      : t("drawResult");
  if (myRatingChange) {
    return t("resultAnnouncementWithRatingTemplate", {
      label,
      resultText,
      before: Math.round(myRatingChange.before),
      after: Math.round(myRatingChange.after),
      delta: formatRatingDelta(myRatingChange.before, myRatingChange.after),
    });
  }
  return t("resultAnnouncementTemplate", { label, resultText });
}

function GameContent({ gameId }: { gameId: string }) {
  const t = useTranslations("game");
  const tNav = useTranslations("NavBar");
  const tConn = useTranslations("ConnectionStatus");
  const tA11y = useTranslations("a11y");
  const { userId } = useAuth();
  const announcer = useAnnouncer();
  const router = useRouter();
  const searchParams = useSearchParams();
  const presetParam = searchParams.get("preset");
  const presetKey: PresetKey =
    presetParam && isPresetKey(presetParam) ? presetParam : "steal1";
  const preset = PRESETS[presetKey];
  const isBotMode = searchParams.get("mode") === "bot";
  const levelParam = parseInt(searchParams.get("level") ?? "3", 10);
  const botLevel = isNaN(levelParam) ? 3 : levelParam;
  const playerSide: Side = "White"; // Simple default: player is White vs Bot

  useEffect(() => {
    if (!isBotMode) return;
    const preset =
      presetParam === "custom" || (presetParam && isPresetKey(presetParam))
        ? presetParam
        : presetKey;
    rememberBotGame({ gameId, preset, level: botLevel });
  }, [isBotMode, gameId, presetParam, presetKey, botLevel]);

  const [state, setState] = useState<GameStateResponse | null>(null);
  const [legalUci, setLegalUci] = useState<string[]>([]);
  const [moves, setMoves] = useState<MoveListEntry[]>([]);
  const [clockHistory, setClockHistory] = useState<ClockHistoryPoint[]>([]);
  const [whitePopups, setWhitePopups] = useState<TimeDeltaPopup[]>([]);
  const [blackPopups, setBlackPopups] = useState<TimeDeltaPopup[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [now, setNow] = useState(0);
  const [drawOfferFrom, setDrawOfferFrom] = useState<Side | null>(null);
  const [presenceNote, setPresenceNote] = useState<string | null>(null);
  const [capturedByWhite, setCapturedByWhite] = useState<PieceChar[]>([]);
  const [capturedByBlack, setCapturedByBlack] = useState<PieceChar[]>([]);
  const [captureSecondsWhite, setCaptureSecondsWhite] = useState(0);
  const [captureSecondsBlack, setCaptureSecondsBlack] = useState(0);
  const [ratingChange, setRatingChange] = useState<GameRatingChange | null>(
    null,
  );
  const [rematchState, setRematchState] = useState<
    "idle" | "offered" | "incoming"
  >("idle");
  /** P-4-1 / P-4-2: what the hub connection is doing, which nothing on this page showed before. */
  const [connState, setConnState] = useState<ConnectionState>("online");

  const connRef = useRef<signalR.HubConnection | null>(null);
  const stateRef = useRef<GameStateResponse | null>(null);
  const syncRef = useRef<ClockSync | null>(null);
  const popupCounter = useRef(0);
  /** First JoinGame on this mount resets move/capture lists; reconnects keep them. */
  const hasJoinedRef = useRef(false);
  /**
   * The previous reading of OUR clock, which is all the urgency announcements need (design
   * decision 3). Storing the reading rather than "the last threshold announced" is what lets
   * `crossedThreshold` treat a crossing as a fact about a pair of readings: a clock that wins time
   * back and spends it again simply crosses twice, with no flag to re-arm and none to leave stuck.
   */
  const prevRemainingRef = useRef<number | null>(null);
  const playSound = useChessSounds();

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

  function spawnPopups(events: ClockEventDto[]) {
    for (const ev of events) {
      if (ev.amount === 0) continue;
      const id = `p${popupCounter.current++}`;
      const popup: TimeDeltaPopup = { id, amount: ev.amount, label: ev.type };
      const setter = ev.side === "White" ? setWhitePopups : setBlackPopups;
      setter((prev) => [...prev, popup]);
      setTimeout(() => {
        setter((prev) => prev.filter((p) => p.id !== id));
      }, 1300);
    }
  }

  // Connect, join, and subscribe. One connection for the life of the page.
  useEffect(() => {
    let cancelled = false;
    hasJoinedRef.current = false;
    const conn = createGameConnection();
    connRef.current = conn;

    conn.on("MovePlayed", (p: MovePlayedDto) => {
      const priorFen = stateRef.current?.fen;
      const prior = stateRef.current?.sideToMove ?? p.state.sideToMove;
      if (priorFen) {
        playSound(soundForMove(priorFen, p.uci));
        if (isCapture(priorFen, p.uci)) {
          const captured = capturedPieceType(priorFen, p.uci);
          if (captured) {
            const setter =
              prior === "White" ? setCapturedByWhite : setCapturedByBlack;
            setter((prev) => [...prev, captured]);
          }
        }
      }

      for (const ev of p.events) {
        if (ev.type === "CaptureGain") {
          const setter =
            ev.side === "White"
              ? setCaptureSecondsWhite
              : setCaptureSecondsBlack;
          setter((prev) => prev + ev.amount);
        }
      }

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
      spawnPopups(p.events);
      setLegalUci(p.legalMoves);
      setDrawOfferFrom(null);
      // Only ever set on the move that actually ends the game (e.g. checkmate): every other move's
      // payload carries no ratingChange, so this is a no-op until the game-ending ply arrives.
      setRatingChange(p.ratingChange ?? null);
      applyState(p.state);

      // One utterance: what moved, what it cost you, what it gave them, and only then the board
      // consequence (design decision 2). `priorFen` is the same capture already made above, reused
      // rather than recomputed. `CheckGain`/`CheckLoss` only appear in `events` when this specific
      // move delivered check, so their presence is the check flag without a separate field.
      if (priorFen) {
        const mySideNow = localPlayerSide(
          p.state.whitePlayerId,
          userId,
          isBotMode,
          playerSide,
        );
        const deliversCheck = p.events.some(
          (ev) => ev.type === "CheckGain" || ev.type === "CheckLoss",
        );
        announcer.polite(
          describeMove({
            priorFen,
            uci: p.uci,
            events: p.events,
            you: mySideNow,
            mover: prior,
            check: deliversCheck,
            t: tA11y,
          }),
        );
      }
    });

    conn.on("GameEnded", (p: GameEndedPayload) => {
      setLegalUci([]);
      setDrawOfferFrom(null);
      setPresenceNote(null);
      setRatingChange(p.ratingChange ?? null);
      applyState(p.state);

      const endedPlayerSide = localPlayerSide(
        p.state.whitePlayerId,
        userId,
        isBotMode,
        playerSide,
      );
      const myEndedRatingChange = p.ratingChange
        ? endedPlayerSide === "White"
          ? p.ratingChange.white
          : p.ratingChange.black
        : null;
      announcer.assertive(resultAnnouncement(p.state, t, myEndedRatingChange));

      if (p.state.winner === null) {
        playSound("draw");
      } else if (p.state.winner === endedPlayerSide) {
        playSound("win");
      } else {
        playSound("loss");
      }
    });

    conn.on("DrawOffered", (p: DrawOfferedPayload) => {
      setDrawOfferFrom(p.bySide);
      // Visual-only until now (the banner below): a draw offer is exactly the kind of thing a
      // screen-reader user must not have to notice on their own.
      announcer.assertive(t("opponentOffersDraw"));
    });
    conn.on("DrawDeclined", () => setError(t("drawOfferDeclined")));
    conn.on("OpponentDisconnected", (p: OpponentDisconnectedPayload) => {
      const current = stateRef.current;
      const solo =
        isBotMode ||
        (current != null && current.whitePlayerId === current.blackPlayerId);
      if (solo) return;
      const message = t("opponentDisconnected", { seconds: p.graceSeconds });
      setPresenceNote(message);
      announcer.assertive(message);
    });
    conn.on("OpponentReconnected", () => {
      const current = stateRef.current;
      const solo =
        isBotMode ||
        (current != null && current.whitePlayerId === current.blackPlayerId);
      if (solo) return;
      setPresenceNote(null);
      announcer.assertive(t("opponentReconnected"));
    });
    conn.on("RematchOffered", () => {
      setRematchState("incoming");
      announcer.assertive(t("rematchOfferedAnnouncement"));
    });
    conn.on("RematchAccepted", (p: RematchAcceptedPayload) => {
      // Both players get this, including the one who offered: only the broadcast, not the accepting
      // player's own invoke, reaches them, so navigation happens from here for both sides rather than
      // from handleAcceptRematch below.
      router.push(`/game/${p.gameId}${p.pool ? `?preset=${p.pool}` : ""}`);
    });

    const join = async () => {
      const snap = (await conn.invoke("JoinGame", gameId)) as GameSnapshot;
      if (cancelled) return;
      const firstJoin = !hasJoinedRef.current;
      hasJoinedRef.current = true;

      // C-1-2: unconditional, on every join including every reconnect. The snapshot carries the
      // game's whole move log now, so this is a rebuild from the authority rather than a reset of
      // state the client alone was keeping - it is idempotent, and re-running it is how a ply that
      // landed while the transport was down gets back into the move list, the clock graph and the
      // captured-material panel. The old guard only skipped the reset, which stopped the sidebar
      // being *blanked* but left it permanently short of those plies, with every later
      // `moveNumber` off by the number missed.
      const history = historyFromSnapshot(snap);
      setMoves(history.moves);
      setClockHistory(history.clockHistory);
      setCapturedByWhite(history.capturedByWhite);
      setCapturedByBlack(history.capturedByBlack);
      setCaptureSecondsWhite(history.captureSecondsWhite);
      setCaptureSecondsBlack(history.captureSecondsBlack);

      // Still first-join only: the snapshot carries no rating change (it is computed once, in the
      // transaction that ended the game, and arrives on GameEnded/MovePlayed), so clearing it on a
      // later join would wipe the result overlay's "+8" on a reconnect after the game finished.
      if (firstJoin) setRatingChange(null);

      setLegalUci(snap.state.status === "Ongoing" ? snap.legalMoves : []);
      applyState(snap.state);
      // P-3-1: cold load of the rematch-mail link never saw RematchOffered. The snapshot carries
      // who offered, including this viewer's own id when they are the one waiting.
      setRematchState(rematchUiState(userId, snap.state.rematchOfferedById));

      if (
        firstJoin &&
        snap.state.status === "Ongoing" &&
        snap.state.moveCount === 0
      ) {
        playSound("start");
      }
    };

    // P-4-1. Same shape as the OpponentDisconnected handler above: set the banner state and say
    // the same sentence assertively, because the player who dropped is the one who can act on it
    // and the only one the server cannot tell.
    conn.onreconnecting(() => {
      setConnState("reconnecting");
      announcer.assertive(tConn("reconnectingWithForfeit"));
    });

    conn.onreconnected(() => {
      // P-4-7. Stay on the reconnecting banner until join actually succeeds, so a failed
      // resync is not announced as "restored". Retry twice on a short delay before Reload.
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

    // P-4-2. End of P-6-5's seven-step reconnect schedule, after which no hub
    // event can arrive. `cancelled` is what tells that apart from the ordinary
    // close the cleanup below performs on unmount.
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

    // P-6-5. iOS freezes reconnect timers while backgrounded. On return, if the
    // schedule already exhausted, start() the same connection and re-join.
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

    const started = (async () => {
      try {
        await conn.start();
        await join();
      } catch (err) {
        if (!cancelled) {
          const message = err instanceof Error ? err.message : "";
          // 10a.2's connection budget, matched on the server's literal text rather than translated
          // copy, the same detect-then-translate shape the watch page uses for "Game not found".
          // A player should only ever meet this if their own address is holding twenty sockets:
          // the anonymous ceiling reserves the rest of the budget for them.
          setError(
            message.includes("Busy right now")
              ? t("serverBusy")
              : message || t("failedToJoinGame"),
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisibility);
      stopWhenSettled(conn, started);
      connRef.current = null;
    };
  }, [
    gameId,
    applyState,
    isBotMode,
    playerSide,
    userId,
    announcer.polite,
    announcer.assertive,
    t,
    tA11y,
    tConn,
    router,
  ]);

  // Clock tick. P-4-8: freeze at the last reading while the hub is down, so a moving clock cannot
  // sell the "still live" illusion. 100ms keeps the tenths (under 20s) on the same tenth the
  // server has; performance.now() so a wall-clock step does not jump the digits.
  useEffect(() => {
    if (connState !== "online") return;
    setNow(performance.now());
    const interval = setInterval(() => setNow(performance.now()), 100);
    return () => clearInterval(interval);
  }, [connState]);

  // Clock-urgency announcements (design decision 3): pushed, assertive, and the only push that
  // interrupts. Reuses the ticker above (`now` ticks every 100ms) rather than a second interval.
  // A crossing is derived from the previous reading and this one, so a stalled tick that jumps a
  // whole second cannot swallow a threshold, and a clock that wins time back and spends it again
  // announces on every crossing without any flag to re-arm (see `crossedThreshold`). Opponent
  // flagfall risk is never announced (see the design section): it isn't actionable at the same
  // urgency, and doubling the interruptions makes the channel useless.
  useEffect(() => {
    if (!state || state.status !== "Ongoing") {
      // A finished or absent game must not leave a stale reading behind: the next game's first
      // tick would compare against it and could announce a crossing that never happened.
      prevRemainingRef.current = null;
      return;
    }
    const mySideNow = localPlayerSide(
      state.whitePlayerId,
      userId,
      isBotMode,
      playerSide,
    );
    const { white, black } = displayTimes();
    const myRemaining = mySideNow === "White" ? white : black;
    const previous = prevRemainingRef.current;
    prevRemainingRef.current = myRemaining;

    if (previous === null) return; // First reading: nothing to have crossed yet.

    const threshold = crossedThreshold(previous, myRemaining);
    if (threshold !== null)
      announcer.assertive(t("urgencyAnnouncement", { threshold }));
    // `displayTimes()` (declared below, but a hoisted function declaration) reads `now` and
    // `state` from this same render's closure; `now` ticking is what actually drives this effect.
  }, [now, state, isBotMode, playerSide, userId, announcer.assertive, t]);

  const handleMove = useCallback(
    async (uci: string) => {
      setError(null);
      const conn = connRef.current;
      // P-4-3. Same Connected guard as OpenSeekList / RatedPoolsGrid: invoke while reconnecting
      // used to dump SignalR's English "Cannot send data..." into the error slot.
      if (!conn || conn.state !== signalR.HubConnectionState.Connected) {
        setError(tConn("reconnectingWithForfeit"));
        return;
      }
      try {
        await conn.invoke("SubmitMove", gameId, uci);
        // State updates arrive via the MovePlayed broadcast (including our own move). The
        // board itself is not touched here even optimistically - `<Board>` uses the fact that
        // this function returns a promise only to dim the clicked-from square until it settles
        // (C-11-7's pending affordance), never to predict the resulting position or clock.
      } catch (err) {
        setError(err instanceof Error ? err.message : t("moveFailed"));
      }
      // Deliberately no rethrow: the catch above always resolves this promise rather than
      // rejecting it, which is what lets `<Board>` treat "settled" as "safe to stop showing
      // pending" without needing its own error handling for this specific caller.
    },
    [gameId, t, tConn],
  );

  // C-1-7: the Stockfish worker belongs to this game and dies with it. It used to be a module
  // singleton that outlived both the game and the route, which is how a search started for one
  // board could answer for another. Declared above the move effect below so that, on the render
  // where both first run, the engine exists before the first search asks for it.
  const botEngineRef = useRef<BotEngine | null>(null);
  useEffect(() => {
    if (!isBotMode) return;
    const engine = createBotEngine();
    botEngineRef.current = engine;
    return () => {
      botEngineRef.current = null;
      engine.dispose();
    };
  }, [isBotMode, gameId]);

  useEffect(() => {
    if (
      isBotMode &&
      state &&
      state.sideToMove !== playerSide &&
      legalUci.length > 0
    ) {
      const signal = { aborted: false };

      // The bot's own remaining clock drives its think-time budget, so it spends time like a
      // player rather than replying instantly. Without this the server floors its sub-second
      // replies to zero charged seconds and the bot can never be flagged (Phase 7.2).
      const botSide: Side = playerSide === "White" ? "Black" : "White";
      const shownNow = syncRef.current
        ? displayClocks(syncRef.current, performance.now(), state.status === "Ongoing")
        : {
            white: state.whiteTime,
            black: state.blackTime,
          };
      const botRemaining = botSide === "White" ? shownNow.white : shownNow.black;
      const thinkMs = simulatedThinkMs({
        remainingSeconds: botRemaining,
        legalMoveCount: legalUci.length,
        elo: botLevel,
      });
      const thinkStartedAt = performance.now();
      const delay = createThinkDelay(thinkMs, signal);

      const onBotVisibility = () => {
        if (signal.aborted || document.visibilityState !== "visible") return;
        const current = stateRef.current;
        if (!current) return;
        const sync = syncRef.current;
        const clocks = sync
          ? displayClocks(sync, performance.now(), current.status === "Ongoing")
          : { white: current.whiteTime, black: current.blackTime };
        const remaining = botSide === "White" ? clocks.white : clocks.black;
        delay.retarget(
          remainingThinkMsAfterWake({
            originalThinkMs: thinkMs,
            elapsedMs: performance.now() - thinkStartedAt,
            remainingSeconds: remaining,
            legalMoveCount: legalUci.length,
            elo: botLevel,
          }),
        );
      };
      document.addEventListener("visibilitychange", onBotVisibility);

      (async () => {
        const engine = botEngineRef.current;
        let chosen: string | null = null;
        try {
          if (!engine) throw new Error("The bot engine is not running");
          // Wait out the budget and search concurrently: the delay is the *floor* on how long the
          // move takes, not an addition to it, so a slow search never double-charges the bot.
          const [botUci] = await Promise.all([
            engine.getMove(state.fen, botLevel, legalUci),
            delay.promise,
          ]);
          chosen = botUci;
        } catch (e) {
          // A search that was superseded or disposed is not an engine failure: a newer effect (or
          // no game at all) owns this turn, so falling back to a random move here would play one
          // for a position that has already moved on.
          if (isBotSearchAbandoned(e)) return;
          console.error("Engine failed, using random move", e);
          chosen = getRandomMove(legalUci);
        }
        if (!signal.aborted && chosen) handleMove(chosen);
      })();

      return () => {
        signal.aborted = true;
        delay.retarget(0);
        document.removeEventListener("visibilitychange", onBotVisibility);
      };
    }
  }, [
    isBotMode,
    state?.sideToMove,
    state?.fen,
    playerSide,
    legalUci,
    handleMove,
    botLevel,
  ]);

  function requireHubConnected(): boolean {
    const conn = connRef.current;
    if (conn && conn.state === signalR.HubConnectionState.Connected) return true;
    setError(tConn("reconnectingWithForfeit"));
    return false;
  }

  async function handleResign() {
    setError(null);
    if (!requireHubConnected()) return;
    const conn = connRef.current;
    if (!conn) return;
    try {
      await conn.invoke("Resign", gameId);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("resignFailed"));
    }
  }

  async function handleOfferDraw() {
    setError(null);
    if (!requireHubConnected()) return;
    const conn = connRef.current;
    if (!conn) return;
    try {
      await conn.invoke("OfferDraw", gameId);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("drawOfferFailed"));
    }
  }

  async function handleRespondDraw(accept: boolean) {
    setError(null);
    if (!requireHubConnected()) return;
    const conn = connRef.current;
    if (!conn) return;
    try {
      await conn.invoke("RespondDraw", gameId, accept);
      setDrawOfferFrom(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("drawResponseFailed"));
    }
  }

  async function handleOfferRematch() {
    setError(null);
    if (!requireHubConnected()) return;
    const conn = connRef.current;
    if (!conn) return;
    try {
      await conn.invoke("OfferRematch", gameId);
      setRematchState("offered");
    } catch (err) {
      setError(err instanceof Error ? err.message : t("rematchOfferFailed"));
    }
  }

  async function handleAcceptRematch() {
    setError(null);
    if (!requireHubConnected()) return;
    const conn = connRef.current;
    if (!conn) return;
    try {
      await conn.invoke("AcceptRematch", gameId);
      // No local state change here: navigation for both players happens off the "RematchAccepted"
      // broadcast above, the accepting player's own invoke resolving is not what moves anyone.
    } catch (err) {
      setError(err instanceof Error ? err.message : t("rematchAcceptFailed"));
    }
  }

  function costFor(from: string, to: string): number | undefined {
    if (!state) return undefined;
    const economy = state.economy ?? economyFromPreset(preset);
    return computeCostPreview(state.fen, from, to, economy).moverNet;
  }

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
      <BoardLoadingShell label={tNav("play")} status={t("loadingGame")} />
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
            <Link href="/" className="btn-primary mt-2 px-6 py-3 text-sm">
              {tNav("play")}
            </Link>
          </EmptyState>
        </main>
      </div>
    );
  }

  const { white, black } = displayTimes();
  const gameOver = state.status !== "Ongoing";
  const connected = connState === "online";
  const playableUci =
    connected && !(isBotMode && state.sideToMove !== playerSide) ? legalUci : [];

  const mySide = localPlayerSide(
    state.whitePlayerId,
    userId,
    isBotMode,
    playerSide,
  );
  const oppSide: Side = mySide === "White" ? "Black" : "White";
  const opponentIsBot =
    oppSide === "White" ? state.whiteViaApi : state.blackViaApi;
  // An offer is actionable only if it isn't our own. The server excludes the offering
  // *connection*, not the offering player, so a second tab open on the same account would
  // otherwise see its own offer as an answerable Accept/Decline.
  const incomingDraw =
    !gameOver && drawOfferFrom !== null && drawOfferFrom !== mySide;
  const myTime = mySide === "White" ? white : black;
  const oppTime = mySide === "White" ? black : white;
  const myPopups = mySide === "White" ? whitePopups : blackPopups;
  const oppPopups = mySide === "White" ? blackPopups : whitePopups;
  const myCaptures = mySide === "White" ? capturedByWhite : capturedByBlack;
  const oppCaptures = mySide === "White" ? capturedByBlack : capturedByWhite;
  const mySecondsWon =
    mySide === "White" ? captureSecondsWhite : captureSecondsBlack;
  const oppSecondsWon =
    mySide === "White" ? captureSecondsBlack : captureSecondsWhite;

  return (
    <div className="board-page-safe-bottom min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      {announcer.node}
      <main
        id="main-content"
        className="page-board flex flex-col py-6 scroll-mt-24"
      >
        <h1 className="sr-only">{tNav("play")}</h1>
        {error && (
          <AlreadyInGameNotice
            message={error}
            className="mb-4 text-sm text-[var(--loss)]"
          />
        )}
        <ConnectionStatus state={connState} forfeitWarning={!gameOver} />
        {presenceNote && <p className="mb-4 notice-banner">{presenceNote}</p>}
        {incomingDraw && (
          <Card
            padding="px-3 py-2"
            className="mb-4 flex flex-wrap items-center gap-3 text-sm"
          >
            <span className="text-[var(--foreground)]">
              {t("opponentOffersDraw")}
            </span>
            <button
              type="button"
              onClick={() => handleRespondDraw(true)}
              disabled={!connected}
              className="btn-primary px-5 py-2 text-sm"
            >
              {t("accept")}
            </button>
            <button
              type="button"
              onClick={() => handleRespondDraw(false)}
              disabled={!connected}
              className="btn-secondary px-5 py-2 text-sm"
            >
              {t("decline")}
            </button>
          </Card>
        )}

        <BoardPageLayout
          opponentClock={
            <div className="flex flex-col gap-2">
              {opponentIsBot && (
                <p className="text-sm font-semibold text-white">
                  {t("opponentIsBot")}
                </p>
              )}
              <Clock
                variant="compact"
                side={oppSide}
                seconds={oppTime}
                active={state.sideToMove === oppSide && !gameOver && connected}
                frozen={!connected}
                popups={oppPopups}
              />
            </div>
          }
          gapMeter={<GapMeter gapSeconds={gapFromClocks(myTime, oppTime)} />}
          localClock={
            <Clock
              variant="compact"
              side={mySide}
              seconds={myTime}
              active={state.sideToMove === mySide && !gameOver && connected}
              frozen={!connected}
              popups={myPopups}
            />
          }
          board={
            <>
              <Board
                fen={state.fen}
                legalUci={playableUci}
                sideToMove={state.sideToMove}
                onMove={handleMove}
                costFor={costFor}
                lastMoveUci={moves.at(-1)?.uci}
                mySide={mySide}
              />

              {/* A second, parallel view over the same state (design decision 1): a real
                  role="grid" of 64 cells next to the drag-and-drop board, which react-chessboard v4
                  cannot be made keyboard-navigable without a fight not worth having. sr-only until
                  Tab reaches it. */}
              <AccessibleBoard
                fen={state.fen}
                legalUci={playableUci}
                sideToMove={state.sideToMove}
                you={mySide}
                onMove={handleMove}
                costFor={costFor}
                onAnnounce={announcer.assertive}
                onRequestClocks={() =>
                  announcer.assertive(describeClocks(myTime, oppTime, tA11y))
                }
                onRequestLastMove={() => {
                  const last = moves[moves.length - 1];
                  if (!last) {
                    announcer.assertive(t("noMovesYet"));
                    return;
                  }
                  const delta =
                    last.side === "White" ? last.whiteDelta : last.blackDelta;
                  announcer.assertive(
                    t("lastMoveAnnouncement", {
                      uci: last.uci,
                      side: last.side,
                      sign: delta >= 0 ? t("plus") : t("minus"),
                      amount: Math.abs(delta),
                    }),
                  );
                }}
              />
            </>
          }
          sidebar={
            <>
              {gameOver ? (
                <GameOverOverlay
                  termination={state.termination}
                  winner={state.winner}
                  history={clockHistory}
                  startTimeSeconds={
                    state.economy?.startTimeSeconds ?? preset.startTimeSeconds
                  }
                  opponentId={
                    state.whitePlayerId === state.blackPlayerId
                      ? null
                      : state.whitePlayerId === userId
                        ? state.blackPlayerId
                        : state.whitePlayerId
                  }
                  gameId={state.gameId}
                  rated={state.rated}
                  mySide={mySide}
                  ratingChange={ratingChange}
                  rematchState={rematchState}
                  rematchDisabled={!connected}
                  onOfferRematch={handleOfferRematch}
                  onAcceptRematch={handleAcceptRematch}
                />
              ) : (
                <GameControls
                  onOfferDraw={handleOfferDraw}
                  onResign={handleResign}
                  disabled={!connected}
                  showDraw={
                    !isBotMode && state.whitePlayerId !== state.blackPlayerId
                  }
                />
              )}

              <CapturedMaterialPanel
                opponentLabel={t("opponentLabel")}
                opponentPieces={oppCaptures}
                opponentSeconds={oppSecondsWon}
                yourPieces={myCaptures}
                yourSeconds={mySecondsWon}
              />

              <MoveList moves={moves} />
            </>
          }
        />
      </main>
    </div>
  );
}

export default function GamePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);
  const tNav = useTranslations("NavBar");
  const t = useTranslations("game");
  return (
    <RequireAuth
      fallback={
        <BoardLoadingShell label={tNav("play")} status={t("loadingGame")} />
      }
    >
      <GameContent gameId={id} />
    </RequireAuth>
  );
}
