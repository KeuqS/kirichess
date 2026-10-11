"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  type ReactNode,
} from "react";
import { useRouter } from "@/i18n/navigation";
import * as signalR from "@microsoft/signalr";
import { createGameConnection, stopWhenSettled } from "@/lib/realtime";
import type { ChallengeAcceptedPayload, MatchFoundPayload } from "@/lib/types";

/**
 * I-1-1 / AS-4. One shared `/hubs/game` connection for the lobby widgets, created on first
 * queue/seek/challenge action rather than on mount. Anonymous visitors never call `ensure`
 * (they are sent to `/login`), so an idle lobby tab holds presence only.
 */
type GameHubApi = {
  ensure: () => Promise<signalR.HubConnection>;
  onReconnect: (handler: () => void) => () => void;
};

const GameHubContext = createContext<GameHubApi | null>(null);

export function GameHubProvider({ children }: { children: ReactNode }) {
  const router = useRouter();
  const connRef = useRef<signalR.HubConnection | null>(null);
  const startedRef = useRef<Promise<unknown> | null>(null);
  const reconnectHandlers = useRef(new Set<() => void>());

  const onReconnect = useCallback((handler: () => void) => {
    reconnectHandlers.current.add(handler);
    return () => {
      reconnectHandlers.current.delete(handler);
    };
  }, []);

  const ensure = useCallback(async () => {
    if (!connRef.current) {
      const conn = createGameConnection();
      connRef.current = conn;
      conn.on("MatchFound", (p: MatchFoundPayload) => {
        router.push(`/game/${p.gameId}?preset=${p.pool}`);
      });
      conn.on("ChallengeAccepted", (p: ChallengeAcceptedPayload) => {
        router.push(`/game/${p.gameId}?preset=${p.pool}`);
      });
      conn.onreconnected(() => {
        for (const handler of reconnectHandlers.current) handler();
      });
      startedRef.current = conn.start().catch(() => {
        // First action retries start() below if this attempt failed.
      });
    }
    const conn = connRef.current;
    await startedRef.current;
    if (conn.state !== signalR.HubConnectionState.Connected) {
      startedRef.current = conn.start();
      await startedRef.current;
    }
    return conn;
  }, [router]);

  useEffect(() => {
    return () => {
      const conn = connRef.current;
      const started = startedRef.current;
      connRef.current = null;
      startedRef.current = null;
      if (conn && started) stopWhenSettled(conn, started);
    };
  }, []);

  return (
    <GameHubContext.Provider value={{ ensure, onReconnect }}>{children}</GameHubContext.Provider>
  );
}

export function useGameHub(): GameHubApi {
  const ctx = useContext(GameHubContext);
  if (!ctx) {
    throw new Error("useGameHub requires GameHubProvider");
  }
  return ctx;
}
