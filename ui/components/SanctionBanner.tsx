"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { IconAlertTriangle } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { useAuth } from "@/components/AuthProvider";
import { getMySanctions } from "@/lib/api";
import type { MySanctionDto, SanctionTypeName } from "@/lib/types";

function expiryLabel(
  expiresAt: string | null,
  t: ReturnType<typeof useTranslations>,
): string {
  if (!expiresAt) return t("doesNotExpire");
  const ms = new Date(expiresAt).getTime() - Date.now();
  if (ms <= 0) return t("expired");
  const days = Math.ceil(ms / (1000 * 60 * 60 * 24));
  if (days <= 1) return t("expiresWithinDay");
  return t("expiresInDays", { days });
}

const TYPE_LABEL_KEY: Record<
  SanctionTypeName,
  "warning" | "ratedPlayBan" | "accountBan"
> = {
  Warning: "warning",
  RatedPlayBan: "ratedPlayBan",
  AccountBan: "accountBan",
};

/**
 * A `RatedPlayBan` otherwise manifests as a bare "You are not currently eligible for rated play."
 * thrown from the matchmaking hub, with no explanation anywhere. This surfaces the moderator's
 * reason directly on the profile, and (S-3-8) on the lobby as well - the cheapest, most
 * proportionate sanction in the vocabulary, a `Warning`, changes nothing about what a player can
 * do, so being shown to them is its entire function, and `/profile` is a page nobody has a
 * particular reason to visit.
 *
 * `MySanctionDto` carries no moderator note and no issuer by construction (see `ui/lib/api.ts`), so
 * there is nothing beyond `reason` to show, and this must not fetch an admin route to enrich it.
 * A `Warning` is shown too: a warning nobody sees is not a warning. Fetch failures render nothing,
 * silently: neither page must break because a moderation call failed.
 *
 * Gated on `hydrated && token` (S-3-8): the lobby is public, so this must not fire
 * `/api/moderation/my-sanctions` - a 401 for every signed-out visitor - before an access token
 * exists to send.
 */
export function SanctionBanner() {
  const t = useTranslations("SanctionBanner");
  const { token, hydrated } = useAuth();
  const [sanctions, setSanctions] = useState<MySanctionDto[]>([]);

  useEffect(() => {
    if (!hydrated || !token) return;

    let cancelled = false;
    getMySanctions()
      .then((data) => {
        if (!cancelled) setSanctions(data);
      })
      .catch(() => {
        // Silent: see doc comment above.
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, token]);

  if (sanctions.length === 0) return null;

  return (
    <div className="flex flex-col gap-3 mb-8">
      {sanctions.map((sanction, i) => (
        <Card key={i} className="flex items-start gap-3">
          <IconAlertTriangle
            size={20}
            stroke={2}
            className="mt-0.5 shrink-0 text-[var(--loss)]"
          />
          <div className="flex flex-col gap-1">
            <div className="text-sm font-bold text-[var(--loss)]">
              {t(TYPE_LABEL_KEY[sanction.type])}
            </div>
            <p className="text-sm text-[var(--foreground)] m-0">
              {sanction.reason}
            </p>
            <span className="text-sm text-muted">
              {expiryLabel(sanction.expiresAt, t)}
            </span>
          </div>
        </Card>
      ))}
    </div>
  );
}
