import { getTranslations } from "next-intl/server";

interface GapRung {
  range: string;
  labelKey: string;
  pct: number;
  intensity: number;
  descriptionKey: string;
}

// Anchored to the C1 reference pool (360s clock): a hung bishop is ~20s of
// gap, a hung queen ~50s (docs/KIRICHESS.md's C2 figures, halved).
const GAP_RUNGS: GapRung[] = [
  {
    range: "0-10s",
    labelKey: "rungANudgeLabel",
    pct: 14,
    intensity: 0.15,
    descriptionKey: "rungANudgeDescription",
  },
  {
    range: "10-25s",
    labelKey: "rungAnEdgeLabel",
    pct: 28,
    intensity: 0.3,
    descriptionKey: "rungAnEdgeDescription",
  },
  {
    range: "25-50s",
    labelKey: "rungPressureLabel",
    pct: 44,
    intensity: 0.45,
    descriptionKey: "rungPressureDescription",
  },
  {
    range: "50-100s",
    labelKey: "rungBleedingLabel",
    pct: 60,
    intensity: 0.6,
    descriptionKey: "rungBleedingDescription",
  },
  {
    range: "100-200s",
    labelKey: "rungCrisisLabel",
    pct: 78,
    intensity: 0.8,
    descriptionKey: "rungCrisisDescription",
  },
  {
    range: "200s+",
    labelKey: "rungKnockoutLabel",
    pct: 100,
    intensity: 1,
    descriptionKey: "rungKnockoutDescription",
  },
];

export async function GapLadder() {
  const t = await getTranslations("GapLadder");
  return (
    <div className="border border-[var(--border)] bg-[var(--surface)] overflow-hidden">
      {GAP_RUNGS.map((rung, i) => (
        <div
          key={rung.labelKey}
          className={`px-5 py-4 ${
            i < GAP_RUNGS.length - 1 ? "border-b border-[var(--border)]/50" : ""
          }`}
        >
          <div className="flex items-center gap-4 mb-1.5">
            <span className="w-20 shrink-0 text-sm tracking-wide text-muted">
              {rung.range}
            </span>
            <div className="flex-1 h-2 bg-[var(--surface-sunken)] overflow-hidden">
              <div
                className="h-full "
                style={{
                  width: `${rung.pct}%`,
                  backgroundColor: `color-mix(in srgb, var(--loss) ${rung.intensity * 100}%, transparent)`,
                }}
              />
            </div>
            <span className="w-24 shrink-0 text-right text-sm font-bold text-white">
              {t(rung.labelKey)}
            </span>
          </div>
          <p className="pl-24 text-sm text-muted leading-relaxed">
            {t(rung.descriptionKey)}
          </p>
        </div>
      ))}
    </div>
  );
}
