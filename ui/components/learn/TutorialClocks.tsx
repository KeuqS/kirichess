"use client";

import { useTranslations } from "next-intl";
import type { Side } from "@/lib/types";
import {
  TimeDeltaLayer,
  type TimeDeltaPopup,
} from "@/components/TimeDeltaLayer";

interface TutorialClockFaceProps {
  side: Side;
  seconds: number;
  active: boolean;
  popups: TimeDeltaPopup[];
}

function formatTime(totalSeconds: number): string {
  const clamped = Math.max(0, Math.floor(totalSeconds));
  const minutes = Math.floor(clamped / 60);
  const secs = clamped % 60;
  return `${minutes}:${secs.toString().padStart(2, "0")}`;
}

/**
 * A local clock face for the tutorial, matching Clock.tsx's visual treatment (mono tabular-nums,
 * text-6xl font-black, bounty border + glow when active, --loss when flagged) without importing it:
 * Clock.tsx belongs to the concurrent Phase 6f effort and may be changing under us.
 */
function TutorialClockFace({
  side,
  seconds,
  active,
  popups,
}: TutorialClockFaceProps) {
  const t = useTranslations("TutorialClocks");
  const flagged = seconds <= 0;
  const sideLabel = side === "White" ? t("white") : t("black");

  return (
    <div
      className={`relative overflow-hidden border px-6 py-8 text-center transition-colors bg-[var(--surface)] ${
        active
          ? "border-[var(--bounty)] shadow-[0_0_30px_-5px_color-mix(in_srgb,var(--bounty)_40%,transparent)]"
          : "border-[var(--border)]"
      }`}
    >
      <div className="text-sm font-semibold uppercase tracking-widest text-muted">
        {sideLabel}
      </div>
      <div
        className={`mt-2 font-mono text-6xl font-black tabular-nums sm:text-7xl ${
          flagged ? "text-[var(--loss)]" : active ? "text-white" : "text-muted"
        }`}
      >
        {formatTime(seconds)}
      </div>
      {flagged && (
        <div className="mt-1 text-sm font-semibold text-[var(--loss)]">
          {t("flagged")}
        </div>
      )}
      <TimeDeltaLayer popups={popups} />
    </div>
  );
}

interface TutorialClocksProps {
  whiteSeconds: number;
  blackSeconds: number;
  /** Which side is "on the move" for this step: highlighted with the active/bounty treatment. */
  activeSide: Side | null;
  whitePopups: TimeDeltaPopup[];
  blackPopups: TimeDeltaPopup[];
  orientation?: "horizontal" | "vertical";
}

/** The two clocks side by side, for the tutorial's board steps and the no-board opener. */
export function TutorialClocks({
  whiteSeconds,
  blackSeconds,
  activeSide,
  whitePopups,
  blackPopups,
  orientation = "horizontal",
}: TutorialClocksProps) {
  return (
    <div
      className={`grid gap-4 ${orientation === "vertical" ? "grid-cols-1" : "grid-cols-2"}`}
    >
      <TutorialClockFace
        side="Black"
        seconds={blackSeconds}
        active={activeSide === "Black"}
        popups={blackPopups}
      />
      <TutorialClockFace
        side="White"
        seconds={whiteSeconds}
        active={activeSide === "White"}
        popups={whitePopups}
      />
    </div>
  );
}
