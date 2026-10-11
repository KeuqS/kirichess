"use client";

import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import { useAuth } from "@/components/AuthProvider";
import { createPresenceConnection, stopWhenSettled } from "@/lib/realtime";
import type { PresencePayload } from "@/lib/types";

const PresenceContext = createContext<PresencePayload | null>(null);

/**
 * Holds the one presence-hub connection for the whole app. It lives in the root layout rather than
 * next to the readout because being connected is what makes the visitor *count* as online: a player
 * sitting on the game page has to stay connected here too, or they'd show up in "in game" while
 * missing from "online". Reconnects when the token changes, since the token carries the identity.
 */
export function PresenceProvider({ children }: { children: ReactNode }) {
  const { token, hydrated } = useAuth();
  const [presence, setPresence] = useState<PresencePayload | null>(null);

  useEffect(() => {
    // Wait for the stored token, otherwise a signed-in visitor connects anonymously and is uncounted.
    if (!hydrated) return;

    const conn = createPresenceConnection();
    conn.on("PresenceChanged", (payload: PresencePayload) =>
      setPresence(payload),
    );
    const started = conn.start().catch(() => setPresence(null));

    return () => {
      conn.off("PresenceChanged");
      stopWhenSettled(conn, started);
    };
  }, [token, hydrated]);

  return (
    <PresenceContext.Provider value={presence}>
      {children}
    </PresenceContext.Provider>
  );
}

/** Live lobby counts, or null until the first push arrives (or if the hub is unreachable). */
export function usePresence(): PresencePayload | null {
  return useContext(PresenceContext);
}
