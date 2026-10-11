"use client";

import { useTranslations } from "next-intl";

interface TutorialProgressProps {
  stepNumber: number; // 1-based
  totalSteps: number;
  onSkip: () => void;
}

/** "Step N of 8" plus a skip control that exits the tutorial entirely. */
export function TutorialProgress({
  stepNumber,
  totalSteps,
  onSkip,
}: TutorialProgressProps) {
  const t = useTranslations("TutorialProgress");
  return (
    <div className="flex items-center justify-between gap-4">
      <div className="flex items-center gap-3">
        <span className="text-sm font-semibold uppercase tracking-widest text-muted">
          {t("stepOf", { stepNumber, totalSteps })}
        </span>
        <div className="flex gap-1">
          {Array.from({ length: totalSteps }, (_, i) => (
            <span
              key={i}
              className={`h-1 w-6 ${
                i < stepNumber
                  ? "bg-[var(--bounty)]"
                  : "bg-[var(--border)]"
              }`}
            />
          ))}
        </div>
      </div>
      <button
        type="button"
        onClick={onSkip}
        className="text-sm font-semibold uppercase tracking-widest text-muted hover:text-[var(--bounty)] transition-colors"
      >
        {t("skipTutorial")}
      </button>
    </div>
  );
}
