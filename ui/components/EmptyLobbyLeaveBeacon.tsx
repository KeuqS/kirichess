"use client";

import { useEffect, useRef } from "react";
import { recordCounter } from "@/lib/api";
import { COUNTER_KEYS } from "@/lib/analytics";
import { shouldRecordEmptyLobbyLeave } from "@/lib/emptyLobbyLeave";

/**
 * P-3-3. Fires empty-lobby-leave once on pagehide from `/` when the seek list was empty and the
 * visitor neither queued, started a game, nor followed an in-origin link. Renders nothing.
 * `ready` must be true only after the first successful seek poll, so a bounce during load is
 * not counted as an empty lobby.
 */
export function EmptyLobbyLeaveBeacon({
  seekCount,
  ready,
}: {
  seekCount: number;
  ready: boolean;
}) {
  const seekCountRef = useRef(seekCount);
  seekCountRef.current = seekCount;
  const readyRef = useRef(ready);
  readyRef.current = ready;
  const internalNavRef = useRef(false);
  const firedRef = useRef(false);

  useEffect(() => {
    function onClick(event: MouseEvent) {
      const target = event.target;
      if (!(target instanceof Element)) return;
      const anchor = target.closest("a[href]");
      if (!(anchor instanceof HTMLAnchorElement)) return;
      if (anchor.origin === window.location.origin) internalNavRef.current = true;
    }

    function onPageHide() {
      if (firedRef.current) return;
      if (
        !shouldRecordEmptyLobbyLeave({
          internalNav: internalNavRef.current,
          seekCount: seekCountRef.current,
          ready: readyRef.current,
        })
      ) {
        return;
      }
      firedRef.current = true;
      recordCounter(COUNTER_KEYS.emptyLobbyLeave);
    }

    document.addEventListener("click", onClick);
    window.addEventListener("pagehide", onPageHide);
    return () => {
      document.removeEventListener("click", onClick);
      window.removeEventListener("pagehide", onPageHide);
    };
  }, []);

  return null;
}
