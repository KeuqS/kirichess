"use client";

import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";
import { PRESETS, isPresetKey } from "@/lib/presets";
import type { RatingDto } from "@/lib/types";

interface PoolRatingCardProps {
  rating: RatingDto;
}

export function PoolRatingCard({ rating }: PoolRatingCardProps) {
  const t = useTranslations("PoolRatingCard");
  const preset = isPresetKey(rating.pool) ? PRESETS[rating.pool] : null;
  const label = preset ? preset.label : rating.pool;
  const time = preset?.time;
  const rounded = Math.round(rating.rating);

  return (
    <Card padding="p-8" className="flex min-w-[min(100%,20rem)] flex-col">
      <div className="flex items-start justify-between gap-x-4">
        <div className="min-w-0">
          <h3 className="text-lg font-bold text-white">{label}</h3>
          {time && (
            <p className="mt-1 text-sm tracking-widest font-semibold text-muted">
              {time}
            </p>
          )}
        </div>
        <div
          className={`text-3xl font-bold whitespace-nowrap ${
            rating.provisional ? "text-[var(--foreground)]/80" : "text-white"
          }`}
        >
          {rounded}
          {rating.provisional ? "?" : ""}
        </div>
      </div>
      <div className="flex items-center justify-between border-t border-[var(--border)] pt-5 mt-6">
        <div className="flex flex-col">
          <span className="text-sm text-muted tracking-widest font-semibold">
            {t("rdLabel")}
          </span>
          <span className="text-base font-bold text-white">
            ±{Math.round(rating.ratingDeviation)}
          </span>
        </div>
        <span className="text-sm tracking-widest font-semibold text-muted">
          {rating.provisional
            ? t("gamesSettling", { games: rating.gamesPlayed })
            : t.rich("gamesEstablished", {
                games: rating.gamesPlayed,
                established: (chunks) => chunks,
              })}
        </span>
      </div>
    </Card>
  );
}
