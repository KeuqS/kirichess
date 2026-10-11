import type { CSSProperties } from "react";
import { BoardPageLayout } from "@/components/BoardPageLayout";

const PULSE = "bg-[var(--border)] animate-pulse opacity-50";

/** One pulsing block. Decorative: the caller supplies the accessible loading name. */
export function SkeletonBlock({
  className = "",
  style,
}: {
  className?: string;
  style?: CSSProperties;
}) {
  return (
    <div aria-hidden="true" className={`${PULSE} ${className}`} style={style} />
  );
}

const DEFAULT_WIDTHS = ["100%", "92%", "96%", "88%"];

export function SkeletonRows({
  count = 4,
  widths,
}: {
  count?: number;
  widths?: string[];
}) {
  const w = widths ?? DEFAULT_WIDTHS;
  return (
    <>
      {Array.from({ length: count }).map((_, i) => (
        <SkeletonBlock
          key={i}
          className="h-[22px] my-3"
          style={{ width: w[i % w.length] }}
        />
      ))}
    </>
  );
}

/** A short wait whose loaded content is a stack of lines. */
export function SkeletonStatus({
  label,
  count = 3,
  widths,
}: {
  label: string;
  count?: number;
  widths?: string[];
}) {
  return (
    <div role="status">
      <span className="sr-only">{label}</span>
      <SkeletonRows count={count} widths={widths} />
    </div>
  );
}

/** The chessboard slot while `Board` itself is still loading. */
export function BoardSkeleton({ label }: { label: string }) {
  return (
    <div
      role="status"
      className={`mx-auto aspect-square w-full max-w-[800px] ${PULSE}`}
    >
      <span className="sr-only">{label}</span>
    </div>
  );
}

/**
 * Live game, spectate, and replay share one shell. The placeholder keeps that
 * shell's columns so the page doesn't jump when the position arrives.
 */
export function BoardPageSkeleton() {
  return (
    <BoardPageLayout
      opponentClock={<SkeletonBlock className="h-14 w-full lg:h-28" />}
      gapMeter={<SkeletonBlock className="h-28 w-full" />}
      localClock={<SkeletonBlock className="h-14 w-full lg:h-28" />}
      board={<SkeletonBlock className="aspect-square w-full" />}
      sidebar={
        <div className="flex flex-col gap-4">
          <SkeletonBlock className="h-24 w-full" />
          <SkeletonBlock className="h-28 w-full" />
          <SkeletonBlock className="h-64 w-full" />
        </div>
      }
    />
  );
}
