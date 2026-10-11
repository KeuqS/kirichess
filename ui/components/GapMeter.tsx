"use client";

import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";

/**
 * Same second-boundary as {@link Clock}: each face floors (and clamps at 0), then we subtract.
 * Rounding the raw difference sat the meter up to half a second ahead of the two clocks.
 */
export function gapFromClocks(mySeconds: number, oppSeconds: number): number {
  return Math.max(0, Math.floor(mySeconds)) - Math.max(0, Math.floor(oppSeconds));
}

export function GapMeter({ gapSeconds }: { gapSeconds: number }) {
  const t = useTranslations("GapMeter");
  const sign = gapSeconds > 0 ? "+" : "";
  const color =
    gapSeconds > 0
      ? "text-[var(--gain)]"
      : gapSeconds < 0
        ? "text-[var(--loss)]"
        : "text-white";

  return (
    <Card>
      <div className="mb-4 text-sm font-semibold tracking-widest text-muted">
        {t("heading")}
      </div>

      <div
        className={`flex h-16 items-center justify-center bg-[var(--background)] text-2xl font-bold ${color}`}
      >
        {sign}
        {gapSeconds}s
      </div>
    </Card>
  );
}
