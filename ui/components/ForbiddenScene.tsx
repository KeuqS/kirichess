"use client";

import { useEffect, useState } from "react";
import { MiniBoard } from "@/components/MiniBoard";

/**
 * Starting position without either d-pawn. The overlay white pawn sits on d7
 * and lunges at the king on e8 - a legal pawn-capture vector that is not a
 * legal move, because you don't capture the king.
 */
export const FORBIDDEN_FEN =
  "rnbqkbnr/ppp1pppp/8/8/8/8/PPP1PPPP/RNBQKBNR w KQkq - 0 1";

/**
 * White pawn from the Wikipedia / Cburnett set (CC BY-SA 3.0), the same
 * drawing `react-chessboard` ships on the live board.
 */
function BoardPawn({ className = "" }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 45 45"
      className={className}
      aria-hidden="true"
    >
      <path
        d="M22.5 9c-2.21 0-4 1.79-4 4 0 .89.29 1.71.78 2.38C17.33 16.5 16 18.59 16 21c0 2.03.94 3.84 2.41 5.03C15.41 27.09 11 31.58 11 39.5h23c0-7.92-4.41-12.41-7.41-13.47C28.06 24.84 29 23.03 29 21c0-2.41-1.33-4.5-3.28-5.62.49-.67.78-1.49.78-2.38 0-2.21-1.79-4-4-4z"
        fill="#fff"
        stroke="#000"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * Decorative 403 board. Hidden from AT on purpose: the page heading carries the
 * joke, and a 64-cell grid plus a lunging pawn would be noise.
 *
 * Laid out as a 10×10 file/rank grid (gutter files and ranks around the 8×8),
 * same shell as NotFoundScene, so the pawn sits on d7 and lunges one square
 * to e8 at the same size as every other square.
 *
 * P-7-1: MiniBoard (64 divs) rather than react-chessboard, which this page never
 * needed to drag.
 */
export function ForbiddenScene() {
  const [lunging, setLunging] = useState(false);

  useEffect(() => {
    const id = window.setTimeout(() => setLunging(true), 1000);
    return () => window.clearTimeout(id);
  }, []);

  return (
    <div aria-hidden="true" className="w-full">
      <div className="relative grid aspect-square w-full grid-cols-10 grid-rows-10">
        <div className="col-span-8 col-start-2 row-span-8 row-start-2 min-h-0 min-w-0 overflow-hidden">
          <MiniBoard fen={FORBIDDEN_FEN} />
        </div>
        <div
          className={`forbidden-pawn pointer-events-none z-10 col-start-5 row-start-3 ${lunging ? "is-lunging" : ""}`}
        >
          <BoardPawn className="block h-full w-full" />
        </div>
      </div>
    </div>
  );
}
