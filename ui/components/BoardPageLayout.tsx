import type { ReactNode } from "react";

interface BoardPageLayoutProps {
  opponentClock: ReactNode;
  localClock: ReactNode;
  gapMeter: ReactNode;
  board: ReactNode;
  sidebar: ReactNode;
  /** Extra classes on the right column (replay's sticky overflow). */
  sidebarClassName?: string;
  /** Watch page: display name above the opponent (Black) clock. */
  opponentHeader?: ReactNode;
  /** Watch page: display name above the local / White clock. */
  localHeader?: ReactNode;
  /** Replay transport: under the board at lg, after the local clock below lg. */
  afterBoard?: ReactNode;
}

/**
 * Shared shell for live game, spectate, and replay.
 *
 * Below lg (1024px), CSS `display: contents` lets the two clocks leave the
 * aside so reading order is opponent clock, board, local clock, then the rest.
 * At lg the wrappers become flex columns again and the grid is the original
 * `240px / 1fr / 320px` split, with the gap meter between the clocks in a
 * sticky left aside.
 */
export function BoardPageLayout({
  opponentHeader,
  opponentClock,
  gapMeter,
  localHeader,
  localClock,
  board,
  afterBoard,
  sidebar,
  sidebarClassName = "",
}: BoardPageLayoutProps) {
  return (
    <div className="grid grid-cols-1 items-start gap-2 lg:grid-cols-[240px_1fr_320px] lg:gap-6 lg:justify-center">
      <aside className="contents lg:flex lg:flex-col lg:gap-3 lg:sticky lg:top-[88px]">
        <div className="order-1 flex flex-col gap-2 lg:order-none lg:gap-3">
          {opponentHeader}
          {opponentClock}
        </div>
        <div className="order-5 lg:order-none">{gapMeter}</div>
        <div className="order-3 flex flex-col gap-2 lg:order-none lg:gap-3">
          {localHeader}
          {localClock}
        </div>
      </aside>

      <div className="contents lg:flex lg:flex-col lg:gap-4">
        <div className="order-2 flex flex-col gap-3 lg:order-none">{board}</div>
        {afterBoard ? (
          <div className="order-4 lg:order-none">{afterBoard}</div>
        ) : null}
      </div>

      <div
        className={`order-6 flex flex-col gap-4 lg:order-none ${sidebarClassName}`}
      >
        {sidebar}
      </div>
    </div>
  );
}
