"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Chessboard } from "react-chessboard";
import { TouchBackend } from "react-dnd-touch-backend";
import type {
  CustomSquareStyles,
  Square as CbSquare,
} from "react-chessboard/dist/chessboard/types";
import { parseBoard } from "@/lib/fen";
import { previewUci } from "@/lib/previewMoves";
import {
  promotionChoices,
  resolveBoardMove,
  type PromotionPickerPiece,
} from "@/lib/boardMoveResolution";
import { BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE } from "@/lib/boardTheme";
import { PromotionPicker } from "@/components/PromotionPicker";
import type { Side } from "@/lib/types";

/**
 * Dest hints and last-move match Lichess chessground / Chess.com: both sites tint
 * the from and to squares of the last ply (not an arrow) until the next one, and
 * paint dests as a centered circle on empty squares / a ring on captures.
 *
 * `backgroundImage` (not `backgroundColor`) so the checkerboard colour stays
 * visible behind the overlay. Last-move uses `--bounty` at the same 41% overlay
 * chessground uses for `square.last-move`. Dest hints are `--hue-green` mixed
 * 66% with black: the plain hue is a light text green, and at 92% the dots
 * washed out on these grey squares. This mix is one green on both square
 * colours, and darker than the text hue without dropping into the forest green
 * that disappears on the dark squares.
 */
const DEST_GREEN = "color-mix(in srgb, var(--hue-green) 66%, black)";
const DEST_DOT = `radial-gradient(circle, ${DEST_GREEN} 22%, transparent 22%)`;
const DEST_CAPTURE = `radial-gradient(circle, transparent 79%, ${DEST_GREEN} 80%)`;
const LAST_MOVE_TINT =
  "linear-gradient(color-mix(in srgb, var(--bounty) 41%, transparent), color-mix(in srgb, var(--bounty) 41%, transparent))";
const SELECTED_INSET =
  "inset 0 0 0 3px color-mix(in srgb, var(--bounty) 85%, transparent)";

export function squareStylesForSelection(
  fen: string,
  legalUci: string[],
  selectedFrom: string | null,
  lastMoveUci: string | null = null,
  /**
   * C-11-7: the origin square of a click move that has been sent to the server and not yet
   * confirmed by the `MovePlayed` broadcast. Dimmed (not tinted, so no new colour token is
   * needed) so a click reads as accepted instead of sitting there looking ignored on a slow
   * link. Piece position is not touched here or anywhere else in this function - the server's
   * `MovePlayed` broadcast is still the only thing that moves a piece (realtime.md /
   * game/[id]/page.tsx's own comment on `handleMove`).
   */
  pendingFrom: string | null = null,
): CustomSquareStyles {
  const styles: CustomSquareStyles = {};

  if (lastMoveUci && lastMoveUci.length >= 4) {
    const from = lastMoveUci.slice(0, 2) as CbSquare;
    const to = lastMoveUci.slice(2, 4) as CbSquare;
    styles[from] = { backgroundImage: LAST_MOVE_TINT };
    styles[to] = { backgroundImage: LAST_MOVE_TINT };
  }

  if (pendingFrom) {
    styles[pendingFrom as CbSquare] = {
      ...styles[pendingFrom as CbSquare],
      opacity: 0.5,
    };
  }

  if (!selectedFrom) return styles;

  const board = parseBoard(fen);
  styles[selectedFrom as CbSquare] = {
    ...styles[selectedFrom as CbSquare],
    boxShadow: SELECTED_INSET,
  };
  for (const to of targetsFor(legalUci, selectedFrom)) {
    if (to === selectedFrom) continue;
    const hint = board[to] ? DEST_CAPTURE : DEST_DOT;
    const prev = styles[to as CbSquare];
    styles[to as CbSquare] = {
      ...prev,
      backgroundImage: prev?.backgroundImage
        ? `${hint}, ${prev.backgroundImage}`
        : hint,
    };
  }
  return styles;
}

interface BoardProps {
  fen: string;
  legalUci: string[];
  sideToMove: Side;
  /**
   * A `Promise` return is optional and used only for C-11-7's pending affordance: when the
   * caller's handler returns one (`game/[id]/page.tsx`'s `handleMove` does, since it awaits
   * `SubmitMove`), the clicked-from square dims until it settles - on success, on a caught
   * error, or on a rejection, since the caller's own try/catch means it always settles rather
   * than hangs. A plain `void`-returning handler (spectator/replay/tutorial boards) opts out
   * automatically; no call site needs to change.
   */
  onMove: (uci: string) => void | Promise<unknown>;
  costFor: (from: string, to: string) => number | undefined;
  /** Replay/observer boards pass false: pieces are not draggable and clicks select nothing (8.2). */
  interactive?: boolean;
  /** Last ply as UCI (`e2e4`, `e7e8q`). Both from and to are tinted, matching Lichess/Chess.com. */
  lastMoveUci?: string | null;
  /**
   * The local player's colour, for a live game. Orients the board so that colour sits at the
   * bottom (like Lichess/Chess.com) and restricts dragging and move entry to that colour's
   * pieces on that colour's turn. While the opponent is thinking, a click may still highlight
   * one of this colour's pieces so the selection is already there when the turn arrives - that
   * highlight is not a premove: nothing is sent until it is this colour's turn and a destination
   * is chosen. Omitted by spectator/replay/tutorial boards (`watch/[id]`, `replay/[id]`,
   * `/learn`), which have no local player and keep today's White-on-bottom, side-to-move-draggable
   * behaviour.
   */
  mySide?: Side;
}

function targetsFor(legalUci: string[], from: string): string[] {
  return legalUci
    .filter((uci) => uci.startsWith(from))
    .map((uci) => uci.slice(2, 4));
}

export function Board({
  fen,
  legalUci,
  sideToMove,
  onMove,
  costFor,
  interactive = true,
  lastMoveUci = null,
  mySide,
}: BoardProps) {
  const [dragFrom, setDragFrom] = useState<string | null>(null);
  // C-11-6: a click onto the last rank with more than one legal promotion piece. Drag still
  // asks through react-chessboard's dialog; this is only the click path, which used to append `q`.
  const [promotion, setPromotion] = useState<{
    from: string;
    to: string;
    pieces: PromotionPickerPiece[];
  } | null>(null);
  // A click that lands while a drag is still in progress belongs to the drop (and, for a
  // promoting pawn, to react-chessboard's own dialog). Opening ours as well would stack two pickers.
  const draggingRef = useRef(false);
  // P-5-7: globals.css's `@media (prefers-reduced-motion: reduce)` block flattens every CSS
  // transition/animation app-wide, which already covers react-chessboard v4's own piece-movement
  // transition. This is belt-and-braces against a future react-chessboard version that animates
  // in JS instead, where the CSS override would have nothing to win against.
  const [reducedMotion, setReducedMotion] = useState(false);
  // C-11-7: origin square of a click move sent to the server and not yet confirmed by
  // `MovePlayed`. Deliberately not a client-side FEN apply - the board's single source of
  // truth stays the server broadcast (see the comment on `handleMove` in
  // `game/[id]/page.tsx`); this only dims the square and blocks further input so the click
  // reads as accepted instead of doing nothing on a slow link.
  const [pendingFrom, setPendingFrom] = useState<string | null>(null);
  // Set in the same turn as `onMove`, before React re-renders. `pendingFrom` alone is too late:
  // react-chessboard delivers `onPieceDrop` and `onSquareClick` for one gesture, and a second
  // drop can land before the state update. Either one would submit a move from the legal list
  // that the in-flight move is about to invalidate.
  const pendingRef = useRef<string | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => {
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    setReducedMotion(query.matches);
    const onChange = (event: MediaQueryListEvent) =>
      setReducedMotion(event.matches);
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, []);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const myColor = mySide === "White" ? "w" : mySide === "Black" ? "b" : null;
  // Opponent's clock is running. Clicks may highlight one of our pieces; they must not submit.
  const waiting = mySide !== undefined && sideToMove !== mySide;
  const placement = useMemo(() => parseBoard(fen), [fen]);
  // A preselection whose piece was captured is no longer a selection. Derived so the highlight
  // drops on the same render as the new FEN, not one frame later.
  const selectedFrom =
    dragFrom && (myColor === null || placement[dragFrom]?.color === myColor)
      ? dragFrom
      : null;

  // Belt-and-braces clear, independent of the promise below settling: any new position from
  // the server (our move landing, the opponent's move, a resync) means whatever we were
  // waiting on is over, so a pending square is never left dimmed once the board has moved on.
  // The same position change drops a preselection whose piece is no longer ours.
  useEffect(() => {
    pendingRef.current = null;
    setPendingFrom(null);
    setPromotion(null);
    setDragFrom((current) => {
      if (!current || !myColor) return current;
      const piece = parseBoard(fen)[current];
      return piece?.color === myColor ? current : null;
    });
  }, [fen, myColor]);

  const legalFromSquares = useMemo(() => {
    const set = new Set<string>();
    // Blank out the off-turn player's view of the side-to-move's pieces (mirrors the bot-mode
    // legalUci blanking already done by the game page for human games, which don't blank it).
    if (waiting) return set;
    for (const uci of legalUci) set.add(uci.slice(0, 2));
    return set;
  }, [legalUci, waiting]);

  // While waiting, legalUci is the opponent's moves, so it cannot paint our dots. Preview the
  // selected piece's own destinations from the current placement. A click still cannot submit
  // one of these until it is our turn and the server's list agrees.
  const destinationUci = useMemo(() => {
    if (!waiting || !myColor) return legalUci;
    return previewUci(fen, myColor);
  }, [fen, legalUci, myColor, waiting]);

  const customSquareStyles = useMemo(
    () =>
      squareStylesForSelection(
        fen,
        destinationUci,
        selectedFrom,
        lastMoveUci,
        pendingFrom,
      ),
    [destinationUci, fen, lastMoveUci, pendingFrom, selectedFrom],
  );

  function isOwnPiece(square: string): boolean {
    return myColor !== null && placement[square]?.color === myColor;
  }

  function handlePieceDragBegin(_piece: string, sourceSquare: string) {
    draggingRef.current = true;
    if (legalFromSquares.has(sourceSquare) || (waiting && isOwnPiece(sourceSquare))) {
      setDragFrom(sourceSquare);
    }
  }

  function armPending(from: string, result: void | Promise<unknown>) {
    // Only a thenable return opts into the pending affordance (see the `onMove` prop
    // doc comment); a `void`-returning handler leaves the board exactly as it was.
    if (!result || typeof (result as Promise<unknown>).then !== "function") return;
    pendingRef.current = from;
    setPendingFrom(from);
    const clear = () => {
      // Guards against the classic unmount race (constraint from C-11-7's fix): if the
      // page has navigated away or the connection effect has torn down by the time this
      // settles, there is no state left to clear safely.
      if (!mountedRef.current) return;
      pendingRef.current = null;
      setPendingFrom(null);
    };
    // `.then(clear, clear)`, not `.finally(clear)`: `.finally()` runs the callback on
    // either outcome but still re-rejects, which would leave an unhandled rejection here
    // since nothing downstream awaits this call. Attaching a rejection handler directly
    // is what makes both exits terminal for this promise. `game/[id]/page.tsx`'s real
    // `handleMove` never actually rejects (its own try/catch turns a failed `SubmitMove`
    // into an error banner), so in production only the fulfilled branch fires; the
    // rejection branch exists so `Board` does not depend on every future caller doing the
    // same catching.
    (result as Promise<unknown>).then(clear, clear);
  }

  function handleSquareClick(square: string) {
    if (!interactive) return;
    if (draggingRef.current) return;
    // C-11-7: a click move already sent is waiting on `MovePlayed`; block further input
    // rather than letting a second click race it against legal-move data that hasn't been
    // updated for the first move yet. The ref covers a drop from the same gesture, which
    // runs before this state update is visible.
    if (pendingRef.current || pendingFrom) return;
    // Several legal promotions: ask. Every other click dismisses a picker that was already open
    // (the overlay usually gets there first; this covers a click the overlay did not swallow).
    if (selectedFrom && square !== selectedFrom && !waiting) {
      const choices = promotionChoices(legalUci, selectedFrom, square);
      if (choices.length > 1) {
        setPromotion({ from: selectedFrom, to: square, pieces: choices });
        return;
      }
    }
    setPromotion(null);

    if (selectedFrom) {
      if (square === selectedFrom) {
        setDragFrom(null);
        return;
      }

      // While waiting, a second click only moves or clears the highlight. Resolving against
      // legalUci here would be a premove, which this board does not play.
      if (!waiting) {
        const uci = resolveBoardMove(fen, legalUci, selectedFrom, square);
        if (uci) {
          const from = selectedFrom;
          setDragFrom(null);
          armPending(from, onMove(uci));
          return;
        }

        // Not a legal move (or a legal castle) from the selected square: if the clicked square is
        // itself somewhere the side to move can move from, treat this as reselecting the origin
        // rather than a rejected move attempt.
        if (legalFromSquares.has(square)) {
          setDragFrom(square);
          return;
        }
      } else if (isOwnPiece(square)) {
        setDragFrom(square);
        return;
      }

      setDragFrom(null);
      return;
    }

    if (waiting) {
      if (isOwnPiece(square)) setDragFrom(square);
      return;
    }

    if (legalFromSquares.has(square)) {
      setDragFrom(square);
    }
  }

  function handlePieceDragEnd() {
    draggingRef.current = false;
    // Releasing a drag while the opponent thinks is how a click on your own piece lands
    // (the piece has to be draggable for react-chessboard to deliver the click at all).
    // Keep the highlight. A drop still cannot submit, so this is not a premove.
    if (waiting) return;
    setDragFrom(null);
  }

  function handlePieceDrop(
    sourceSquare: string,
    targetSquare: string,
    piece: string,
  ): boolean {
    if (!interactive || pendingRef.current || pendingFrom || waiting) return false;
    setPromotion(null);
    setDragFrom(null);

    const uci = resolveBoardMove(
      fen,
      legalUci,
      sourceSquare,
      targetSquare,
      piece,
    );
    if (!uci) {
      return false;
    }

    armPending(sourceSquare, onMove(uci));
    return true;
  }

  function choosePromotion(piece: PromotionPickerPiece) {
    if (!promotion || pendingRef.current) return;
    const uci = `${promotion.from}${promotion.to}${piece}`;
    if (!legalUci.includes(uci)) {
      setPromotion(null);
      return;
    }
    const from = promotion.from;
    setPromotion(null);
    setDragFrom(null);
    armPending(from, onMove(uci));
  }

  function isDraggablePiece({ piece }: { piece: string }): boolean {
    if (!interactive || pendingFrom) return false;
    const color = piece[0].toLowerCase();
    const isMine =
      (mySide === "White" && color === "w") ||
      (mySide === "Black" && color === "b");
    // Off-turn pieces must stay draggable or react-chessboard never calls onPieceClick /
    // onSquareClick for them: the drag source is what the pointer hits. Drops are rejected
    // in handlePieceDrop, so dragging one cannot play a move.
    if (waiting) return isMine;
    const isSideToMove =
      (sideToMove === "White" && color === "w") ||
      (sideToMove === "Black" && color === "b");
    if (!isSideToMove) return false;
    if (!mySide) return true;
    return isMine;
  }

  const promotingPiece = promotion ? placement[promotion.from] : null;

  return (
    <div className="board-viewport-cap relative">
      <Chessboard
        customDarkSquareStyle={{ backgroundColor: BOARD_DARK_SQUARE }}
        customLightSquareStyle={{ backgroundColor: BOARD_LIGHT_SQUARE }}
        customDndBackend={TouchBackend}
        customDndBackendOptions={{
          enableMouseEvents: true,
          delayTouchStart: 50,
        }}
        position={fen}
        onPieceDrop={handlePieceDrop}
        onPieceDragBegin={handlePieceDragBegin}
        onPieceDragEnd={handlePieceDragEnd}
        onPieceClick={(_piece, square) => handleSquareClick(square)}
        onSquareClick={handleSquareClick}
        isDraggablePiece={isDraggablePiece}
        customSquareStyles={customSquareStyles}
        boardOrientation={mySide === "Black" ? "black" : "white"}
        animationDuration={reducedMotion ? 0 : 300}
      />
      {promotion && promotingPiece && (
        <PromotionPicker
          to={promotion.to}
          pieces={promotion.pieces}
          color={promotingPiece.color}
          orientation={mySide === "Black" ? "black" : "white"}
          onChoose={choosePromotion}
          onCancel={() => setPromotion(null)}
        />
      )}
    </div>
  );
}
