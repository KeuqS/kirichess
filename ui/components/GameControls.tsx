"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Dialog } from "@/components/Dialog";

interface GameControlsProps {
  onOfferDraw: () => void;
  onResign: () => void;
  disabled: boolean;
  /** False for bot/solo games: nobody else can accept a draw. Resign stays. */
  showDraw?: boolean;
}

export function GameControls({
  onOfferDraw,
  onResign,
  disabled,
  showDraw = true,
}: GameControlsProps) {
  const t = useTranslations("GameControls");
  const [confirming, setConfirming] = useState(false);

  function confirmResign() {
    setConfirming(false);
    onResign();
  }

  return (
    <>
      <div className="flex gap-2.5">
        {showDraw && (
          <button
            type="button"
            onClick={onOfferDraw}
            disabled={disabled}
            className="btn-secondary min-h-11 flex-1 py-2.5 text-sm"
          >
            {t("offerDraw")}
          </button>
        )}
        <button
          type="button"
          onClick={() => setConfirming(true)}
          disabled={disabled}
          className="btn-danger min-h-11 flex-1 py-2.5 text-sm"
        >
          {t("resign")}
        </button>
      </div>
      {confirming && (
        <Dialog title={t("confirmTitle")} onClose={() => setConfirming(false)}>
          <p className="mt-2 text-sm leading-relaxed text-[var(--foreground)]">
            {t("confirmBody")}
          </p>
          <div className="mt-6 flex items-center gap-3">
            <button
              type="button"
              onClick={() => setConfirming(false)}
              className="btn-secondary min-h-11 flex-1 py-2.5 text-sm"
            >
              {t("cancel")}
            </button>
            <button
              type="button"
              onClick={confirmResign}
              className="btn-danger-fill min-h-11 flex-1 py-2.5 text-sm"
            >
              {t("confirm")}
            </button>
          </div>
        </Dialog>
      )}
    </>
  );
}
