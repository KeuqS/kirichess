"use client";

import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";
import { SkeletonBlock } from "@/components/SkeletonRows";
import { PoolBadge } from "@/components/PoolBadge";
import { PRESETS, economyOf, type PresetKey } from "@/lib/presets";

/**
 * A rated-pool tile. Clicking it joins (or leaves) the matchmaking queue for that pool: the tile
 * itself holds no queue state or connection; that lives in RatedPoolsGrid, which owns the one
 * SignalR connection shared across all six tiles and passes each tile its own slice of it.
 */
export function PresetTile({
  presetKey,
  rating,
  ratingPending = false,
  searching,
  offerBot,
  onToggle,
  onPlayBot,
}: {
  presetKey: PresetKey;
  rating: number | null;
  /** Signed-in rating is still in flight. Guests stay on the dash. */
  ratingPending?: boolean;
  searching: boolean;
  offerBot?: boolean;
  onToggle: () => void;
  onPlayBot?: () => void;
}) {
  const t = useTranslations("PresetTile");
  const tLoading = useTranslations("loading");
  const preset = PRESETS[presetKey];
  const economy = economyOf(preset);

  return (
    <Card
      padding="p-8"
      className={`relative flex flex-col transition-all duration-200 ${
        searching
          ? "border-[var(--bounty)]/60 shadow-[0_0_16px_-4px_var(--bounty)]"
          : "hover:border-[var(--bounty)]/50 hover:shadow-[0_0_16px_-4px_var(--bounty)]"
      }`}
    >
      {/* Overlay covers the card so a click anywhere queues (or cancels). Game Info sits
          above it so it stays a real link, putting role="button" on Card would nest that
          link and trip axe's nested-interactive rule. aria-label is also what
          e2e/tests/critical-path.spec.ts locates on. */}
      <button
        type="button"
        onClick={onToggle}
        aria-label={
          searching
            ? t("searchingAriaLabel", { name: preset.label })
            : t("queueAriaLabel", { name: preset.label })
        }
        className="absolute inset-0 z-10 cursor-pointer "
      />
      <div className="flex flex-wrap items-baseline justify-between gap-x-4">
        <h3 className="text-2xl font-bold text-white">{preset.label}</h3>
        <span className="text-2xl font-bold text-white whitespace-nowrap">
          {preset.time}
        </span>
      </div>
      <div className="mt-4 flex items-center gap-2">
        <PoolBadge economy={economy} variant="plain" />
        <span aria-hidden="true" className="text-sm text-muted">
          -
        </span>
        <span className="inline-flex items-center text-sm tracking-widest font-semibold text-muted">
          {preset.multiplier}
        </span>
      </div>
      <div className="flex flex-wrap items-center justify-between gap-3 border-t border-[var(--border)] pt-5 mt-6">
        <div className="flex flex-col">
          <span className="text-sm text-muted tracking-widest font-semibold">
            {searching ? t("statusLabel") : t("ratingLabel")}
          </span>
          <span
            className={`text-base font-bold ${
              searching
                ? "text-[var(--bounty)]"
                : rating === null
                  ? "text-muted"
                  : "text-white"
            }`}
          >
            {searching ? (
              t("searchingText")
            ) : ratingPending ? (
              <>
                <span className="sr-only">{tLoading("heading")}</span>
                <SkeletonBlock className="mt-1 h-5 w-16" />
              </>
            ) : (
              (rating ?? "-")
            )}
          </span>
        </div>
        <Link
          href={`/rules#pool-${presetKey}`}
          className="btn-secondary relative z-20 px-3 py-1.5 text-sm"
        >
          {t("gameInfo")}
        </Link>
      </div>
      {searching && offerBot && onPlayBot ? (
        <button
          type="button"
          onClick={onPlayBot}
          aria-label={t("playBotAriaLabel", { name: preset.label })}
          className="btn-secondary relative z-20 mt-4 self-start px-5 py-2 text-sm"
        >
          {t("playBotMeanwhile")}
        </button>
      ) : null}
    </Card>
  );
}
