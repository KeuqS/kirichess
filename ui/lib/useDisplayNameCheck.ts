"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { checkDisplayName } from "./api";
import type { DisplayNameRejection, DisplayNameVerdict } from "./types";

const DEBOUNCE_MS = 400;
/** Below the server's `MinLength`: nothing to learn, no reason to spend a request. */
const MIN_LENGTH = 3;

/**
 * Maps a server `rejection` slug to its key in the `DisplayNameRejection` message namespace.
 * `cooldown` is a valid slug on the wire type but `CheckDisplayName` never returns it (cooldown is
 * decided by the rename endpoint, not this advisory check), so it has no translated string here.
 */
const REJECTION_KEYS: Partial<
  Record<DisplayNameRejection, "tooShort" | "tooLong" | "illegalCharacter" | "separatorPlacement" | "notEnoughSubstance" | "reserved" | "profanity" | "taken">
> = {
  "too-short": "tooShort",
  "too-long": "tooLong",
  "illegal-character": "illegalCharacter",
  "separator-placement": "separatorPlacement",
  "not-enough-substance": "notEnoughSubstance",
  reserved: "reserved",
  profanity: "profanity",
  taken: "taken",
};

/**
 * Debounced, stale-response-safe wrapper around `checkDisplayName` (Phase 8a.4). Shared by the
 * register form (TASK-D step 4) and the profile rename card (step 5) so there is exactly one
 * implementation of "cancel a slow response for an earlier keystroke": a slow response for "Bo"
 * must never overwrite a fast one for "Bob".
 *
 * A sequence-number ref (rather than an `AbortController`) is used because `request()` in
 * `ui/lib/api.ts` doesn't take a signal; the effect's own cleanup also cancels the pending
 * `setTimeout`, so a fast typist never even fires the earlier request.
 */
export function useDisplayNameCheck(candidate: string) {
  const t = useTranslations("DisplayNameRejection");
  const [verdict, setVerdict] = useState<DisplayNameVerdict | null>(null);
  const [checking, setChecking] = useState(false);
  const seqRef = useRef(0);

  useEffect(() => {
    const trimmed = candidate.trim();
    if (trimmed.length < MIN_LENGTH) {
      seqRef.current += 1; // invalidate any in-flight check
      setVerdict(null);
      setChecking(false);
      return;
    }

    const seq = ++seqRef.current;
    setChecking(true);

    const timer = setTimeout(() => {
      checkDisplayName(trimmed)
        .then((res) => {
          if (seqRef.current !== seq) return;
          // The server's `message` is hardcoded English (rendered verbatim elsewhere in this app
          // by design); this one live-feedback surface types with the reader, so it's the
          // exception that gets translated client-side off the stable `rejection` slug rather than
          // echoing the wire string.
          const key = res.rejection
            ? REJECTION_KEYS[res.rejection as DisplayNameRejection]
            : undefined;
          const message = key
            ? t(key, { count: res.lengthLimit ?? 0 })
            : res.message;
          setVerdict({ ...res, message });
        })
        .catch(() => {
          // Advisory only: a failed check must never block or mislead the user.
          if (seqRef.current === seq) setVerdict(null);
        })
        .finally(() => {
          if (seqRef.current === seq) setChecking(false);
        });
    }, DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
    };
  }, [candidate]);

  return { verdict, checking };
}
