"use client";

import { useEffect, useRef } from "react";
import { useTranslations } from "next-intl";

export interface MoveListEntry {
  moveNumber: number;
  side: "White" | "Black";
  uci: string;
  /**
   * Standard algebraic notation ("Nxd5"), when the caller has it. Only the replay does (9e.2): a
   * live move arrives over `MoveResponse` and the hub, neither of which carries SAN until 9e.9
   * settles the hub payload contract. Absent means "show the UCI", not "no notation".
   */
  san?: string;
  whiteDelta: number;
  blackDelta: number;
}

interface MoveListProps {
  moves: MoveListEntry[];
  /** 1-based ply currently shown; highlights that row. Omit for the live game's append-only list, which stays pinned to the newest move. */
  selectedPly?: number;
  /** When provided, rows become buttons that jump the replay to that ply (8.2). */
  onSelectPly?: (ply: number) => void;
}

export function MoveList({ moves, selectedPly, onSelectPly }: MoveListProps) {
  const t = useTranslations("MoveList");
  const selectedRef = useRef<HTMLLIElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    selectedRef.current?.scrollIntoView({ block: "nearest" });
  }, [selectedPly]);

  useEffect(() => {
    if (selectedPly != null) return;
    const list = listRef.current;
    if (!list) return;
    list.scrollTop = list.scrollHeight;
  }, [moves.length, selectedPly]);

  return (
    <div className="flex flex-col border border-[var(--border)] bg-[var(--surface)]">
      <div className="border-b border-[var(--border)] px-4 py-2 text-sm font-semibold uppercase tracking-wide text-muted">
        {t("moves")}
      </div>
      <div ref={listRef} className="max-h-[21rem] overflow-y-auto px-4 py-2">
        {moves.length === 0 && (
          <p className="text-sm text-muted">{t("noMovesYet")}</p>
        )}
        <ol className="flex flex-col gap-1 text-sm">
          {moves.map((move) => {
            const delta =
              move.side === "White" ? move.whiteDelta : move.blackDelta;
            const isSelected = move.moveNumber === selectedPly;
            const content = (
              <>
                <span className="text-[var(--foreground)]">
                  {Math.ceil(move.moveNumber / 2)}
                  {move.side === "White" ? "." : "…"} {move.san ?? move.uci}
                </span>
                <span
                  className={
                    delta >= 0 ? "text-[var(--gain)]" : "text-[var(--loss)]"
                  }
                >
                  {delta >= 0 ? "+" : ""}
                  {delta}s
                </span>
              </>
            );
            return (
              <li
                key={`${move.moveNumber}-${move.side}-${move.uci}`}
                ref={isSelected ? selectedRef : undefined}
                // The zebra stripe and the selection highlight are mutually exclusive, not layered:
                // `odd:bg-[var(--surface-sunken)]` compiles to a `:nth-child(odd)` rule, which
                // outranks a plain background class, so layering them would silently drop the
                // highlight on every odd-numbered ply.
                className={`${
                  isSelected
                    ? "bg-[var(--bounty)]/15 text-white"
                    : "odd:bg-[var(--surface-sunken)]"
                }`}
                aria-current={isSelected ? "true" : undefined}
              >
                {onSelectPly ? (
                  <button
                    type="button"
                    onClick={() => onSelectPly(move.moveNumber)}
                    className="flex w-full items-center justify-between gap-2 px-2 py-1 text-left"
                  >
                    {content}
                  </button>
                ) : (
                  <div className="flex items-center justify-between gap-2 px-2 py-1">
                    {content}
                  </div>
                )}
              </li>
            );
          })}
        </ol>
      </div>
    </div>
  );
}
