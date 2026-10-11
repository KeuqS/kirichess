"use client";

import { useCallback, useEffect, useRef } from "react";
import { SOUND_SRC, type ChessSound } from "./chessSounds";

/**
 * Preloads the chess sound set once per mount and returns a stable `playSound`.
 * `play()` failures (autoplay policy, missing file) are swallowed the way the live
 * game page always has: a muted board is better than an uncaught rejection.
 */
export function useChessSounds() {
  const refs = useRef<Partial<Record<ChessSound, HTMLAudioElement>>>({});

  useEffect(() => {
    const audio: Partial<Record<ChessSound, HTMLAudioElement>> = {};
    for (const type of Object.keys(SOUND_SRC) as ChessSound[]) {
      audio[type] = new Audio(SOUND_SRC[type]);
    }
    refs.current = audio;
  }, []);

  return useCallback((type: ChessSound) => {
    const el = refs.current[type];
    if (!el) return;
    el.currentTime = 0;
    el.play().catch(() => {});
  }, []);
}
