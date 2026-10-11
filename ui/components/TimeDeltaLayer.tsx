"use client";

export interface TimeDeltaPopup {
  id: string;
  amount: number; // signed seconds
  label?: string;
}

interface TimeDeltaLayerProps {
  popups: TimeDeltaPopup[];
}

/** Floating +Ns / -Ns popups rendered over a Clock. Each popup rises + fades via CSS animation. */
export function TimeDeltaLayer({ popups }: TimeDeltaLayerProps) {
  return (
    <div className="pointer-events-none absolute inset-x-0 top-2 flex flex-col items-center gap-1">
      {popups.map((popup) => (
        <span
          key={popup.id}
          className={`time-delta-popup text-lg font-bold ${
            popup.amount >= 0 ? "text-gain" : "text-loss"
          }`}
        >
          {popup.amount >= 0 ? "+" : ""}
          {popup.amount}s
          {popup.label ? (
            <span className="ml-1 text-sm font-normal">
              {popup.label}
            </span>
          ) : null}
        </span>
      ))}
    </div>
  );
}
