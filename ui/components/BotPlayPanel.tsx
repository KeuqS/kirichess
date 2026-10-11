"use client";

import { useState } from "react";
import { usePathname, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { BotGameDialog } from "@/components/BotGameDialog";
import { Card } from "@/components/Card";
import { loginNextHref } from "@/lib/challengeLink";
import { PRESETS, PRESET_ORDER, type PresetKey } from "@/lib/presets";

/**
 * Replaces the old rated-matchmaking queue panel (that's the tile grid now, see
 * RatedPoolsGrid) with an instant, unrated vs-bot picker: choose a pool, set a strength on the
 * popup slider, play immediately. No queue, no SignalR: see BotGameDialog for why bot games can
 * never be rated regardless (7.3: client-side Stockfish is permanently unverifiable).
 */
export function BotPlayPanel() {
  const t = useTranslations("BotPlayPanel");
  const router = useRouter();
  const pathname = usePathname();
  const { token } = useAuth();
  const [dialogPool, setDialogPool] = useState<PresetKey | null>(null);

  function openDialog(pool: PresetKey) {
    if (!token) {
      router.push(loginNextHref(pathname));
      return;
    }
    setDialogPool(pool);
  }

  return (
    <Card className="flex flex-col gap-4">
      <div>
        <h2 className="text-lg font-bold text-white">{t("heading")}</h2>
        {!token && (
          <p className="mt-2 text-sm text-muted leading-relaxed">
            {t("signInToPlay")}
          </p>
        )}
      </div>
      <div className="flex flex-wrap gap-3">
        {PRESET_ORDER.map((pool) => (
          <button
            key={pool}
            type="button"
            onClick={() => openDialog(pool)}
            aria-label={
              token
                ? undefined
                : t("signInToPlayAria", { pool: PRESETS[pool].label })
            }
            className="btn-secondary px-3 py-1.5 text-sm"
          >
            {PRESETS[pool].label}
          </button>
        ))}
      </div>
      {dialogPool && (
        <BotGameDialog
          presetKey={dialogPool}
          onClose={() => setDialogPool(null)}
        />
      )}
    </Card>
  );
}
