"use client";

import { useState, type PointerEvent } from "react";
import { useTranslations } from "next-intl";

export interface ClockHistoryPoint {
  moveNumber: number;
  whiteTime: number;
  blackTime: number;
}

interface ClockGraphProps {
  history: ClockHistoryPoint[];
  startTimeSeconds: number;
}

const WIDTH = 640;
const HEIGHT = 280;
/** Plot inset only - axis labels sit in HTML so they do not scale with the viewBox. */
const PAD_LEFT = 8;
const PAD_RIGHT = 8;
const PAD_TOP = 8;
const PAD_BOTTOM = 8;

const TICK_LABEL = "text-sm tabular-nums text-muted";

/** Map a viewBox X to the nearest history index. Exported so hover tests don't need layout. */
export function nearestClockPointIndex(
  viewBoxX: number,
  pointCount: number,
): number {
  if (pointCount <= 1) return 0;
  const inner = WIDTH - PAD_LEFT - PAD_RIGHT;
  const step = inner / (pointCount - 1);
  const i = Math.round((viewBoxX - PAD_LEFT) / step);
  return Math.max(0, Math.min(pointCount - 1, i));
}

function xAt(index: number, pointCount: number): number {
  if (pointCount <= 1) return PAD_LEFT;
  const inner = WIDTH - PAD_LEFT - PAD_RIGHT;
  return PAD_LEFT + (index * inner) / (pointCount - 1);
}

function yAt(value: number, maxY: number): number {
  const inner = HEIGHT - PAD_TOP - PAD_BOTTOM;
  return HEIGHT - PAD_BOTTOM - (value / maxY) * inner;
}

function buildPoints(values: number[], maxY: number): string {
  if (values.length === 0) return "";
  return values
    .map((value, i) => {
      const x = xAt(i, values.length);
      const y = yAt(value, maxY);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}

function yTickValues(maxY: number): number[] {
  const mid = Math.round(maxY / 2);
  const ticks = [maxY, mid, 0];
  return [...new Set(ticks.map((v) => Math.round(v)))];
}

function xTickIndices(count: number): number[] {
  if (count <= 1) return [0];
  if (count <= 4) return Array.from({ length: count }, (_, i) => i);
  const last = count - 1;
  return [
    ...new Set([0, Math.round(last / 3), Math.round((2 * last) / 3), last]),
  ];
}

export function ClockGraph({ history, startTimeSeconds }: ClockGraphProps) {
  const t = useTranslations("ClockGraph");
  const [hover, setHover] = useState<{
    i: number;
    left: number;
    top: number;
  } | null>(null);

  if (history.length === 0) {
    return <p className="text-sm text-muted">{t("noHistory")}</p>;
  }

  const maxY =
    Math.max(
      startTimeSeconds,
      ...history.map((h) => Math.max(h.whiteTime, h.blackTime)),
    ) || 1;
  const whitePoints = buildPoints(
    history.map((h) => h.whiteTime),
    maxY,
  );
  const blackPoints = buildPoints(
    history.map((h) => h.blackTime),
    maxY,
  );
  const yTicks = yTickValues(maxY);
  const xTicks = xTickIndices(history.length);

  function onPointerMove(e: PointerEvent<SVGSVGElement>) {
    const rect = e.currentTarget.getBoundingClientRect();
    if (rect.width === 0) return;
    const viewBoxX = ((e.clientX - rect.left) / rect.width) * WIDTH;
    const i = nearestClockPointIndex(viewBoxX, history.length);
    setHover({
      i,
      left: e.clientX - rect.left,
      top: e.clientY - rect.top,
    });
  }

  const hovered = hover ? history[hover.i] : null;

  return (
    <div className="relative w-full">
      <div className="flex items-stretch gap-2">
        <div className="relative w-8 shrink-0">
          {yTicks.map((value) => (
            <span
              key={`y-${value}`}
              className={`absolute right-0 -translate-y-1/2 ${TICK_LABEL}`}
              style={{ top: `${(yAt(value, maxY) / HEIGHT) * 100}%` }}
            >
              {Math.round(value)}
            </span>
          ))}
        </div>
        <div className="relative min-w-0 flex-1">
          <svg
            viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
            className="w-full"
            style={{ aspectRatio: `${WIDTH} / ${HEIGHT}` }}
            role="img"
            aria-label={t("chartAriaLabel")}
            onPointerMove={onPointerMove}
            onPointerLeave={() => setHover(null)}
          >
            <line
              x1={PAD_LEFT}
              y1={HEIGHT - PAD_BOTTOM}
              x2={WIDTH - PAD_RIGHT}
              y2={HEIGHT - PAD_BOTTOM}
              stroke="var(--border)"
              strokeWidth={1}
            />
            <line
              x1={PAD_LEFT}
              y1={PAD_TOP}
              x2={PAD_LEFT}
              y2={HEIGHT - PAD_BOTTOM}
              stroke="var(--border)"
              strokeWidth={1}
            />
            {yTicks.map((value) => (
              <line
                key={`grid-${value}`}
                x1={PAD_LEFT}
                y1={yAt(value, maxY)}
                x2={WIDTH - PAD_RIGHT}
                y2={yAt(value, maxY)}
                stroke="var(--border)"
                strokeWidth={1}
                opacity={0.4}
              />
            ))}
            <polyline
              points={whitePoints}
              fill="none"
              stroke="#ededed"
              strokeWidth={2}
            />
            <polyline
              points={blackPoints}
              fill="none"
              stroke="var(--bounty)"
              strokeWidth={2}
            />
            {hover && hovered && (
              <circle
                cx={xAt(hover.i, history.length)}
                cy={yAt(hovered.whiteTime, maxY)}
                r={4}
                fill="#ededed"
              />
            )}
            {hover && hovered && (
              <circle
                cx={xAt(hover.i, history.length)}
                cy={yAt(hovered.blackTime, maxY)}
                r={4}
                fill="var(--bounty)"
              />
            )}
          </svg>
          {hover && hovered && (
            <div
              className="pointer-events-none absolute z-10 border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-sm text-white shadow-2xl"
              style={{
                left: hover.left,
                top: hover.top,
                transform: "translate(8px, -8px)",
              }}
            >
              {t("hover", {
                move: hovered.moveNumber,
                white: Math.round(hovered.whiteTime),
                black: Math.round(hovered.blackTime),
              })}
            </div>
          )}
          <div className="relative mt-1 h-5">
            {xTicks.map((i) => (
              <span
                key={`x-${i}`}
                className={`absolute -translate-x-1/2 ${TICK_LABEL}`}
                style={{
                  left: `${(xAt(i, history.length) / WIDTH) * 100}%`,
                }}
              >
                {history[i].moveNumber}
              </span>
            ))}
          </div>
        </div>
      </div>
      <div className="mt-2 flex gap-4 text-sm text-muted">
        <span className="flex items-center gap-1">
          <span className="inline-block h-2 w-2 bg-[#ededed]" /> {t("white")}
        </span>
        <span className="flex items-center gap-1">
          <span className="inline-block h-2 w-2 bg-[var(--bounty)]" />{" "}
          {t("black")}
        </span>
      </div>
    </div>
  );
}
