"use client";

import { useTranslations } from "next-intl";
import { usePresence } from "@/components/PresenceProvider";

/**
 * Live "players online / in game" readout for the home screen, plus the weekly play hour.
 * Presence is pushed over the hub (no polling). The hour always renders: it answers "when
 * next?" even before the first presence push, which is the question Presence alone cannot.
 */
export function PresenceIndicator() {
  const t = useTranslations("PresenceIndicator");
  const presence = usePresence();

  return (
    <div
      className="flex flex-wrap items-center gap-x-2.5 gap-y-1 text-sm text-muted"
      aria-live="polite"
    >
      {presence ? (
        <>
          <span className="inline-flex h-2 w-2 bg-[var(--gain)]" />
          <span>
            <span className="text-white">{presence.online.toLocaleString()}</span>{" "}
            {t("online")}
          </span>
          <span aria-hidden="true">-</span>
          <span>
            <span className="text-white">{presence.inGame.toLocaleString()}</span>{" "}
            {t("inGame")}
          </span>
          <span aria-hidden="true">-</span>
        </>
      ) : null}
      <span>{t("playHour")}</span>
    </div>
  );
}
