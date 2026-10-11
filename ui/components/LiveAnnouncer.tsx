"use client";

import { useCallback, useState, type ReactNode } from "react";

/**
 * Two live regions, always mounted (never appearing at the same moment as their text: a screen
 * reader ignores a live region that shows up already populated). Polite carries move and economy
 * announcements (design decision 2); assertive carries clock urgency, check, draw offers and game
 * end (design decision 3). Match `ChallengeFriendPanel.tsx:273`'s existing live-region shape.
 */
interface Region {
  text: string;
  /**
   * Re-announcing the identical string is a real case here (the clock can cross "10 seconds" more
   * than once in one game, see the threshold reset rule). React only touches the DOM when the text
   * node actually changes, so announcing the same string twice in a row would otherwise be silent
   * the second time. Flipping a trailing zero-width space (`​`, invisible and unspoken) on
   * every call guarantees the rendered string changes even when the message doesn't.
   */
  flip: boolean;
}

const ZERO_WIDTH_SPACE = "​";

const EMPTY_REGION: Region = { text: "", flip: false };

export interface Announcer {
  /** Move and economy announcements: one utterance, polite. */
  polite: (message: string) => void;
  /** Clock urgency, check, draw offers, game end and on-demand queries: assertive. */
  assertive: (message: string) => void;
  /** Mount once, near the top of the page. Both regions live here, always in the DOM. */
  node: ReactNode;
}

export function useAnnouncer(): Announcer {
  const [politeRegion, setPoliteRegion] = useState<Region>(EMPTY_REGION);
  const [assertiveRegion, setAssertiveRegion] = useState<Region>(EMPTY_REGION);

  const polite = useCallback((message: string) => {
    setPoliteRegion((prev) => ({ text: message, flip: !prev.flip }));
  }, []);

  const assertive = useCallback((message: string) => {
    setAssertiveRegion((prev) => ({ text: message, flip: !prev.flip }));
  }, []);

  const node = (
    <>
      <span
        role="status"
        aria-live="polite"
        aria-atomic="true"
        className="sr-only"
      >
        {politeRegion.text}
        {politeRegion.flip ? ZERO_WIDTH_SPACE : ""}
      </span>
      <span
        role="alert"
        aria-live="assertive"
        aria-atomic="true"
        className="sr-only"
      >
        {assertiveRegion.text}
        {assertiveRegion.flip ? ZERO_WIDTH_SPACE : ""}
      </span>
    </>
  );

  return { polite, assertive, node };
}
