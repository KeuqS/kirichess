"use client";

import { useTranslations } from "next-intl";
import { PoolBadge } from "@/components/PoolBadge";
import {
  captureEconomy,
  multiplierLabel,
  timeControlLabel,
} from "@/lib/presets";
import type { EconomyConfig } from "@/lib/types";

/**
 * C-8-2. The rules a custom challenge is asking someone to accept, and the same rules a replay of
 * that game can state afterwards. Official pools already render this as a preset tile; this is the
 * summary for a ruleset that has no published page.
 */
export function RuleSummary({
  economy,
  showTime = false,
}: {
  economy: EconomyConfig;
  /** The accept page already prints the clock beside the title. A replay does not. */
  showTime?: boolean;
}) {
  const t = useTranslations("ruleSummary");
  const labels = useTranslations("custom");
  const kind = captureEconomy(
    economy.captureCaptorGainEnabled,
    economy.captureVictimLossEnabled,
  );
  const scale = economy.economyScale;

  let moveCost: string;
  switch (economy.moveCostMode) {
    case "None":
      moveCost = t("moveCostNone");
      break;
    case "Flat":
      moveCost = t("moveCostFlat", {
        seconds: economy.moveCostFlat * scale,
      });
      break;
    case "PerPiece":
      moveCost = t("moveCostPerPiece");
      break;
    case "Distance":
      moveCost = t("moveCostDistance", {
        seconds: economy.moveCostPerSquare * scale,
      });
      break;
    case "PerPieceXDistance":
      moveCost = t("moveCostPerPieceDistance");
      break;
  }

  return (
    <div>
      <div className={`flex flex-wrap items-center gap-2 ${showTime ? "" : "mt-3"}`}>
        {kind ? (
          <PoolBadge economy={kind} variant="plain" />
        ) : (
          <span className="text-sm font-semibold tracking-widest text-muted">
            {t("noCapture")}
          </span>
        )}
        <span aria-hidden="true" className="text-sm text-muted">
          -
        </span>
        <span className="inline-flex items-center text-sm font-semibold tracking-widest text-muted">
          {multiplierLabel(economy.captureMult, economy.economyScale)}
        </span>
      </div>
      <dl className="mt-4 flex flex-col gap-2 border-t border-[var(--border)]/50 pt-4">
        {showTime && (
          <RuleRow
            label={t("timeControl")}
            value={timeControlLabel(
              economy.startTimeSeconds,
              economy.baseIncrement,
            )}
          />
        )}
        <RuleRow label={labels("moveCostModeLabel")} value={moveCost} />
        <RuleRow
          label={labels("repeatedPositionLabel")}
          value={economy.repeatedPositionNullifiesTime ? t("on") : t("off")}
        />
        <RuleRow
          label={labels("threefoldLabel")}
          value={economy.threefoldRepetitionIsDraw ? t("on") : t("off")}
        />
        <RuleRow
          label={labels("flagFallLosesLabel")}
          value={economy.flagFallLoses ? t("on") : t("off")}
        />
        <RuleRow
          label={labels("flagVsInsufficientLabel")}
          value={
            economy.flagVsInsufficientMaterialIsDraw ? t("on") : t("off")
          }
        />
      </dl>
    </div>
  );
}

function RuleRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-4 text-sm">
      <dt className="text-muted">{label}</dt>
      <dd className="shrink-0 text-right font-medium text-white">{value}</dd>
    </div>
  );
}
