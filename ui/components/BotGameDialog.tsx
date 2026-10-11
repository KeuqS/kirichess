"use client";

import { useState, type CSSProperties } from "react";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { ApiError, createGame } from "@/lib/api";
import { markGameStarted } from "@/lib/emptyLobbyLeave";
import { PRESETS, type PresetKey } from "@/lib/presets";
import { usePointerRangeDrag } from "@/hooks/usePointerRangeDrag";

interface BotGameDialogProps {
  presetKey: PresetKey;
  onClose: () => void;
}

/**
 * Strength-picker popup for a bot game, opened from BotPlayPanel. Same modal shell as
 * ReportDialog. Creates an ordinary solo game server-side (opponentId: null, rated: false. The
 * server has no concept of "bot", see GameEndpoints.cs) and only encodes the bot in the client-side
 * `mode=bot&level=` query params the game page already reads.
 */
export function BotGameDialog({ presetKey, onClose }: BotGameDialogProps) {
  const t = useTranslations("BotGameDialog");
  const router = useRouter();
  const preset = PRESETS[presetKey];
  const [level, setLevel] = useState(1500);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const rangeDrag = usePointerRangeDrag({
    min: 400,
    max: 3000,
    step: 50,
    onChange: setLevel,
  });

  async function handleStart() {
    if (creating) return;
    setCreating(true);
    setError(null);
    try {
      const state = await createGame({
        opponentId: null,
        preset: presetKey,
        rated: false,
      });
      markGameStarted();
      router.push(
        `/game/${state.gameId}?preset=${presetKey}&mode=bot&level=${level}`,
      );
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : err instanceof Error
            ? err.message
            : t("createGameFailed"),
      );
      setCreating(false);
    }
  }

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/70 px-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="bot-game-dialog-title"
        className="w-full max-w-md border border-[var(--border)] bg-[var(--surface)] p-6 text-left shadow-2xl"
      >
        <h3 id="bot-game-dialog-title" className="text-lg font-bold text-white">
          {t("playAgainstBot", { preset: preset.label })}
        </h3>
        <p className="mt-1 text-sm text-muted">
          {t("clockAndUnrated", { minutes: preset.startTimeSeconds / 60 })}
        </p>
        <p className="mt-1 text-sm text-muted leading-relaxed">
          {t.rich("clockNote", {
            budget: (chunks) => (
              <Link
                href="/rules#budget"
                className="underline underline-offset-2 decoration-current/40 transition-colors hover:text-[var(--bounty)] hover:decoration-[var(--bounty)]"
              >
                {chunks}
              </Link>
            ),
          })}
        </p>

        <div className="mt-5">
          <div className="flex items-center justify-between text-sm text-muted mb-2">
            <span>{t("botStrength")}</span>
            <span className="text-white font-semibold">
              {t("elo", { level })}
            </span>
          </div>
          <input
            ref={rangeDrag.ref}
            onPointerDown={rangeDrag.onPointerDown}
            type="range"
            aria-label={t("botStrength")}
            min={400}
            max={3000}
            step={50}
            value={level}
            onChange={(e) => setLevel(parseInt(e.target.value, 10))}
            className="range-fill w-full accent-[var(--bounty)]"
            style={
              {
                "--range-progress": `${((level - 400) / (3000 - 400)) * 100}%`,
              } as CSSProperties
            }
          />
        </div>

        {error && (
          <AlreadyInGameNotice
            message={error}
            className="mt-3 text-sm text-[var(--loss)]"
          />
        )}

        <div className="mt-6 flex justify-end gap-3">
          <button
            onClick={onClose}
            disabled={creating}
            className="btn-secondary px-3 py-1"
          >
            {t("cancel")}
          </button>
          <button
            onClick={handleStart}
            disabled={creating}
            className="btn-primary px-6 py-2"
          >
            {creating ? t("starting") : t("startGame")}
          </button>
        </div>
      </div>
    </div>
  );
}
