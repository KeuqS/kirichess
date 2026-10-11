"use client";

import { useEffect, useRef, type KeyboardEvent } from "react";
import { useTranslations } from "next-intl";
import { PieceIcon } from "@/components/PieceIcon";
import { BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE } from "@/lib/boardTheme";
import {
  promotionPickerLayout,
  type PromotionPickerPiece,
} from "@/lib/boardMoveResolution";
import { trapTabKey } from "@/lib/focusTrap";
import type { Color } from "@/lib/fen";

interface PromotionPickerProps {
  to: string;
  pieces: readonly PromotionPickerPiece[];
  color: Color;
  orientation: "white" | "black";
  onChoose: (piece: PromotionPickerPiece) => void;
  onCancel: () => void;
}

/**
 * Same dark-square parity as `MiniBoard`: a1 is dark. The picker paints the algebraic
 * square it covers, so a white queen and a black knight both keep the contrast the board
 * squares were picked for. `--surface` would swallow the black pieces.
 */
function squareIsDark(square: string): boolean {
  const fileIdx = square.charCodeAt(0) - "a".charCodeAt(0);
  const rankIdx = 8 - Number(square[1]);
  return (fileIdx + rankIdx) % 2 === 1;
}

/**
 * Click-path promotion (C-11-6). Drag already asks, through react-chessboard's own dialog,
 * which only opens from a drop and always offers all four pieces. A click has to ask here,
 * and only for the pieces the position actually allows.
 *
 * Labels are the existing `a11y` piece names, so this adds no catalogue strings. The glyphs
 * are the choice a sighted player reads; the names are for the buttons.
 */
export function PromotionPicker({
  to,
  pieces,
  color,
  orientation,
  onChoose,
  onCancel,
}: PromotionPickerProps) {
  const t = useTranslations("a11y");
  const dialogRef = useRef<HTMLDivElement>(null);
  const firstButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const previouslyFocused =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    firstButtonRef.current?.focus();
    return () => {
      if (previouslyFocused && document.body.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === "Escape") onCancel();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onCancel]);

  const layout = promotionPickerLayout(to, orientation, pieces.length);
  const file = to.charCodeAt(0) - "a".charCodeAt(0);
  const fileFromLeft = orientation === "white" ? file : 7 - file;

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key !== "Tab" || !dialogRef.current) return;
    event.stopPropagation();
    trapTabKey(dialogRef.current, event);
  }

  return (
    <div
      className="absolute inset-0 z-10"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onCancel();
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-label={t("promoteOn", { square: to })}
        onKeyDown={handleKeyDown}
        className={`absolute flex shadow-2xl ${layout.growsDown ? "flex-col" : "flex-col-reverse"}`}
        style={{
          left: `${fileFromLeft * 12.5}%`,
          width: "12.5%",
          top: layout.growsDown ? 0 : undefined,
          bottom: layout.growsDown ? undefined : 0,
        }}
      >
        {pieces.map((piece, index) => {
          const square = layout.squares[index];
          return (
            <button
              key={piece}
              ref={index === 0 ? firstButtonRef : undefined}
              type="button"
              aria-label={t(`piece.${piece}`)}
              onClick={() => onChoose(piece)}
              className="flex aspect-square w-full items-center justify-center p-1 shadow-[inset_0_0_0_1px_var(--border)] hover:shadow-[inset_0_0_0_3px_color-mix(in_srgb,var(--bounty)_85%,transparent)]"
              style={{
                backgroundColor: squareIsDark(square)
                  ? BOARD_DARK_SQUARE
                  : BOARD_LIGHT_SQUARE,
              }}
            >
              <PieceIcon type={piece} color={color} className="h-[85%] w-[85%]" />
            </button>
          );
        })}
      </div>
    </div>
  );
}
