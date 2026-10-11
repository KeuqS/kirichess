"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { PatronIcon, isPatronTier } from "@/components/PatronIcon";
import { useAuth } from "@/components/AuthProvider";
import { getMyPatronStatus } from "@/lib/api";
import type { PatronStatusDto } from "@/lib/types";

/** Same shape as `MatchHistoryCard`'s local `formatDate`: this file must not export it either. */
function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

/**
 * The own-profile patron badge (`GET /api/donations/me`). Sits in the header meta row
 * with the joined date and games, and keeps its own trailing separator.
 * Renders nothing when signed out, when the call fails, or when the tier is "None": a profile page
 * must not break because a badge call failed, the same contract `SanctionBanner` follows for the
 * moderation call next to it on this page.
 */
export function PatronBadge() {
  const t = useTranslations("profile");
  const { userId, hydrated } = useAuth();
  const [status, setStatus] = useState<PatronStatusDto | null>(null);

  useEffect(() => {
    if (!hydrated || !userId) return;
    let cancelled = false;
    getMyPatronStatus()
      .then((data) => {
        if (!cancelled) setStatus(data);
      })
      .catch(() => {
        // Silent: see doc comment above.
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, userId]);

  if (!status || !isPatronTier(status.tier)) return null;

  const note =
    status.kind === "Monthly"
      ? t("patronBadgeMonthly")
      : status.expiresAt
        ? t("patronBadgeExpires", { date: formatDate(status.expiresAt) })
        : null;

  return (
    <>
      <span className="mr-2 inline-flex align-middle">
        <PatronIcon tier={status.tier} kind={status.kind} size={16} />
      </span>
      {note && <span>{note}</span>}
      <span className="whitespace-nowrap">
        <span aria-hidden="true" className="mx-4">
          -
        </span>
      </span>
    </>
  );
}
