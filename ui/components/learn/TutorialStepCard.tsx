"use client";

import type { ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";

interface TutorialStepCardProps {
  title: string;
  body: string;
  /** Revealed once the step's move has been played (or immediately for copy-only steps). */
  payoff: string;
  payoffVisible: boolean;
  onBack: () => void;
  onNext: () => void;
  backDisabled: boolean;
  nextDisabled: boolean;
  isLastStep: boolean;
  /** Replaces the Next/Finish button entirely (the final step's Play/Rules links). */
  nextSlot?: ReactNode;
}

/** Title, body, payoff (revealed after the move) and Back/Next controls. Presentational only. */
export function TutorialStepCard({
  title,
  body,
  payoff,
  payoffVisible,
  onBack,
  onNext,
  backDisabled,
  nextDisabled,
  isLastStep,
  nextSlot,
}: TutorialStepCardProps) {
  const t = useTranslations("TutorialStepCard");
  return (
    <Card className="flex flex-col gap-4">
      <div aria-live="polite">
        <h1 className="text-2xl font-bold tracking-tight text-white text-balance mb-4">
          {title}
        </h1>
        <p className="text-sm text-[var(--foreground)]">{body}</p>
        {payoffVisible && payoff && (
          <p className="mt-4 border-t border-[var(--border)] pt-4 text-sm font-semibold text-[var(--bounty)]">
            {payoff}
          </p>
        )}
      </div>

      <div className="mt-2 flex items-center justify-between gap-4">
        <button
          type="button"
          onClick={onBack}
          disabled={backDisabled}
          className="btn-secondary px-5 py-2 text-sm"
        >
          {t("back")}
        </button>
        {nextSlot ?? (
          <button
            type="button"
            onClick={onNext}
            disabled={nextDisabled}
            className="btn-primary px-5 py-2 text-sm"
          >
            {isLastStep ? t("finish") : t("next")}
          </button>
        )}
      </div>
    </Card>
  );
}
