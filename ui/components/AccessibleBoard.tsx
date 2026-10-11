"use client";

import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useTranslations } from "next-intl";
import {
  describePromotionPrompt,
  describeSelection,
  describeSquare,
  describeTarget,
  FILES,
  RANKS,
  type PromotionPiece,
} from "@/lib/a11y";
import { fileIndex, pieceAt, rankIndex, type Square } from "@/lib/fen";
import { previewUci } from "@/lib/previewMoves";
import { GLYPHS } from "@/lib/pieceGlyphs";
import type { Side } from "@/lib/types";

interface AccessibleBoardProps {
  fen: string;
  legalUci: string[];
  sideToMove: Side;
  you: Side;
  onMove: (uci: string) => void;
  costFor: (from: string, to: string) => number | undefined;
  /** Replay and watch pass false: navigation and reading only, no selection, no move entry. */
  interactive?: boolean;
  onAnnounce: (message: string) => void;
  /**
   * Not in the plan's illustrative prop block, added because 'c'/'m' need data
   * (clock seconds, the last move) that this component is never given: `AccessibleBoardProps`
   * only carries board state. Both are optional; omitted, the keys are simply no-ops. The game
   * page supplies them (it already has `displayTimes()` and the move list); replay/watch don't.
   */
  onRequestClocks?: () => void;
  onRequestLastMove?: () => void;
}

/** e2e4 or e2-e4, case-insensitive. A missing promotion piece still means queen: this is the typed shortcut, and the click path asks instead. */
function normalizeEntry(raw: string): string {
  return raw.trim().toLowerCase().replace(/-/g, "");
}

/**
 * The fast path for a practised user: type a move instead of arrowing to it. Deliberately not a
 * parser, just enough validation to give a spoken reason for a rejection rather than clearing
 * silently (see the design section: silence on a bad move is the most common failure in this
 * pattern).
 */
function MoveEntryInput({
  legalUci,
  onMove,
  onAnnounce,
}: {
  legalUci: string[];
  onMove: (uci: string) => void;
  onAnnounce: (message: string) => void;
}) {
  const t = useTranslations("a11yBoard");
  const [value, setValue] = useState("");

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    const normalized = normalizeEntry(value);
    if (!/^[a-h][1-8][a-h][1-8][qrbn]?$/.test(normalized)) {
      onAnnounce(t("invalidMoveForm", { value }));
      return;
    }
    const uci =
      legalUci.find((u) => u === normalized) ??
      legalUci.find((u) => u.startsWith(normalized) && u.endsWith("q"));
    if (!uci) {
      onAnnounce(t("noLegalMove", { value }));
      return;
    }
    onMove(uci);
    setValue("");
  }

  return (
    <form onSubmit={handleSubmit} className="mt-3 flex items-center gap-2">
      <label htmlFor="a11y-move-input" className="text-sm text-muted">
        {t("moveLabel")}
      </label>
      <input
        id="a11y-move-input"
        type="text"
        value={value}
        onChange={(event) => setValue(event.target.value)}
        placeholder={t("placeholder")}
        className="w-24 border border-[var(--border)] bg-[var(--background)] px-2 py-1 text-sm text-[var(--foreground)]"
      />
      <button type="submit" className="btn-secondary px-3 py-1.5 text-sm">
        {t("play")}
      </button>
    </form>
  );
}

export function AccessibleBoard({
  fen,
  legalUci,
  sideToMove,
  you,
  onMove,
  costFor,
  interactive = true,
  onAnnounce,
  onRequestClocks,
  onRequestLastMove,
}: AccessibleBoardProps) {
  const tBoard = useTranslations("a11yBoard");
  const tA11y = useTranslations("a11y");
  const [focused, setFocused] = useState<Square>("e1");
  const [selectedFrom, setSelectedFrom] = useState<Square | null>(null);
  const [pendingPromotion, setPendingPromotion] = useState<{
    from: Square;
    to: Square;
  } | null>(null);
  const cellRefs = useRef<Map<Square, HTMLDivElement | null>>(new Map());

  const legalOrigins = useMemo(
    () => new Set(legalUci.map((u) => u.slice(0, 2))),
    [legalUci],
  );

  const myTurn = sideToMove === you;

  const legalTargets = useMemo(() => {
    if (!selectedFrom) return new Set<Square>();
    // Off-turn legalUci belongs to the opponent. Preview this piece's own squares so the grid
    // matches the dots on the visual board. Enter still does not play them until it is our turn.
    const uci = myTurn ? legalUci : previewUci(fen, you === "White" ? "w" : "b");
    return new Set(
      uci.filter((u) => u.startsWith(selectedFrom)).map((u) => u.slice(2, 4)),
    );
  }, [fen, legalUci, myTurn, selectedFrom, you]);

  function owns(square: Square): boolean {
    const piece = pieceAt(fen, square);
    if (!piece) return false;
    return you === "White" ? piece.color === "w" : piece.color === "b";
  }

  // The opponent captured the held piece, or the square is otherwise no longer ours.
  useEffect(() => {
    if (!selectedFrom) return;
    const piece = pieceAt(fen, selectedFrom);
    const stillOurs =
      piece !== null &&
      (you === "White" ? piece.color === "w" : piece.color === "b");
    if (stillOurs) return;
    setSelectedFrom(null);
    setPendingPromotion(null);
    onAnnounce(tBoard("selectionCleared"));
  }, [fen, onAnnounce, selectedFrom, tBoard, you]);

  // Roving tabindex: moving focus programmatically (not just the React state that drives
  // tabIndex) is what actually carries screen-reader and sighted-keyboard focus to the new cell.
  // All 64 cells exist in the DOM at all times (only the wrapping <section>'s visibility toggles
  // on focus-within), so the ref is always populated and this never races the render that adds it.
  function moveFocusTo(square: Square) {
    setFocused(square);
    cellRefs.current.get(square)?.focus();
  }

  function legalTargetCount(from: Square): number {
    return new Set(
      legalUci.filter((u) => u.startsWith(from)).map((u) => u.slice(2, 4)),
    ).size;
  }

  function promotionMoves(from: Square, to: Square): string[] {
    return legalUci.filter(
      (u) => u.startsWith(`${from}${to}`) && u.length === 5,
    );
  }

  function selectOrigin(square: Square) {
    setPendingPromotion(null);
    setSelectedFrom(square);
    onAnnounce(describeSelection(square, legalTargetCount(square), tA11y));
  }

  function selectHeld(square: Square) {
    setPendingPromotion(null);
    setSelectedFrom(square);
    const count = new Set(
      previewUci(fen, you === "White" ? "w" : "b")
        .filter((u) => u.startsWith(square))
        .map((u) => u.slice(2, 4)),
    ).size;
    onAnnounce(describeSelection(square, count, tA11y));
  }

  function clearSelection() {
    setPendingPromotion(null);
    setSelectedFrom(null);
    onAnnounce(tBoard("selectionCleared"));
  }

  function playPromotion(piece: PromotionPiece) {
    if (!pendingPromotion) return;
    const uci = `${pendingPromotion.from}${pendingPromotion.to}${piece}`;
    if (!legalUci.includes(uci)) {
      onAnnounce(
        tBoard("cannotPromote", { piece: tA11y(`piece.${piece}`) }),
      );
      return;
    }
    onMove(uci);
    setPendingPromotion(null);
    setSelectedFrom(null);
  }

  function playOrPromptMove(from: Square, to: Square) {
    const promotions = promotionMoves(from, to);
    if (promotions.length > 1) {
      const pieces = promotions.map((u) => u[4].toLowerCase() as PromotionPiece);
      setPendingPromotion({ from, to });
      onAnnounce(describePromotionPrompt(to, pieces, tA11y));
      return;
    }
    const candidate =
      promotions.length === 1 ? promotions[0] : `${from}${to}`;
    if (legalUci.includes(candidate)) {
      onMove(candidate);
      setPendingPromotion(null);
      setSelectedFrom(null);
    } else {
      // Never fail silently: an unannounced illegal move is the single most common
      // accessibility failure in a grid-navigation pattern like this one.
      onAnnounce(tBoard("noLegalMoveFromTo", { from, to }));
    }
  }

  function handleActivate() {
    if (!interactive) return;

    if (selectedFrom) {
      if (focused === selectedFrom) {
        // Re-activating your own origin toggles the selection off, matching Board.tsx's
        // click-to-deselect behaviour.
        clearSelection();
        return;
      }
      if (!myTurn) {
        // Same contract as the visual board: while waiting, Enter only moves or clears the
        // highlight. It never plays, and it never selects an opponent piece (those squares are
        // in legalOrigins, which is the side to move).
        if (owns(focused)) selectHeld(focused);
        else clearSelection();
        return;
      }
      if (legalOrigins.has(focused)) {
        // Changing your mind mid-selection: jump the origin to the newly-activated piece instead
        // of treating it as a move attempt, same as Board.tsx's handleSquareClick.
        selectOrigin(focused);
        return;
      }
      playOrPromptMove(selectedFrom, focused);
      return;
    }

    if (!myTurn) {
      if (owns(focused)) selectHeld(focused);
      return;
    }

    if (legalOrigins.has(focused)) {
      selectOrigin(focused);
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    // Never swallow a modifier combination: a bare letter is a board shortcut, the same letter
    // with Ctrl/Alt/Meta held is almost certainly a browser or OS shortcut.
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    if (pendingPromotion) {
      const lower = event.key.toLowerCase();
      if (lower === "q" || lower === "n" || lower === "b" || lower === "r") {
        event.preventDefault();
        playPromotion(lower);
        return;
      }
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        playPromotion("q");
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        setPendingPromotion(null);
        onAnnounce(tBoard("promotionCancelled"));
        return;
      }
      // Arrow / Home / End / Page* cancel the prompt then fall through so the cursor still moves.
      if (
        event.key.startsWith("Arrow") ||
        event.key === "Home" ||
        event.key === "End" ||
        event.key === "PageUp" ||
        event.key === "PageDown"
      ) {
        setPendingPromotion(null);
        onAnnounce(tBoard("promotionCancelled"));
      }
    }

    const file = fileIndex(focused);
    const rank = rankIndex(focused);

    switch (event.key) {
      case "ArrowUp":
        event.preventDefault();
        // Boards have real edges: no wrapping past rank 8 or rank 1, file a or file h.
        if (rank < 7) moveFocusTo(`${FILES[file]}${rank + 2}`);
        break;
      case "ArrowDown":
        event.preventDefault();
        if (rank > 0) moveFocusTo(`${FILES[file]}${rank}`);
        break;
      case "ArrowLeft":
        event.preventDefault();
        if (file > 0) moveFocusTo(`${FILES[file - 1]}${rank + 1}`);
        break;
      case "ArrowRight":
        event.preventDefault();
        if (file < 7) moveFocusTo(`${FILES[file + 1]}${rank + 1}`);
        break;
      case "Home":
        event.preventDefault();
        moveFocusTo(`${FILES[0]}${rank + 1}`);
        break;
      case "End":
        event.preventDefault();
        moveFocusTo(`${FILES[7]}${rank + 1}`);
        break;
      case "PageUp":
        event.preventDefault();
        moveFocusTo(`${FILES[file]}8`);
        break;
      case "PageDown":
        event.preventDefault();
        moveFocusTo(`${FILES[file]}1`);
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        handleActivate();
        break;
      case "Escape":
        event.preventDefault();
        if (interactive && selectedFrom) {
          clearSelection();
        }
        break;
      case "c":
      case "C":
        event.preventDefault();
        onRequestClocks?.();
        break;
      case "m":
      case "M":
        event.preventDefault();
        onRequestLastMove?.();
        break;
      case "?":
        event.preventDefault();
        onAnnounce(tBoard("keySummary"));
        break;
    }
  }

  return (
    <section
      aria-label={
        interactive
          ? tBoard("sectionInteractive", { side: you })
          : tBoard("sectionReadOnly")
      }
      // sr-only until Tab reaches it, then visible: a screen-reader user finds it through the
      // tree either way, and a sighted keyboard-only user sees it appear at the moment they Tab
      // in, rather than tabbing into a control they can't see (design decision 1).
      className="sr-only focus-within:not-sr-only focus-within:mt-4 focus-within:block focus-within:w-full focus-within:max-w-[420px] focus-within:border focus-within:border-[var(--border)] focus-within:bg-[var(--surface)] focus-within:p-3"
    >
      <p className="mb-2 text-sm text-muted">
        {interactive
          ? `${sideToMove === you ? tBoard("yourMove") : tBoard("waiting")} ${tBoard("interactiveHelp")}`
          : tBoard("readOnlyHelp")}
      </p>

      <div
        role="grid"
        aria-label={tBoard("gridLabel")}
        onKeyDown={handleKeyDown}
        className="grid grid-cols-8 border border-[var(--border)]"
      >
        {RANKS.map((rank) => (
          <div role="row" key={rank} className="contents">
            {FILES.map((file) => {
              const square = `${file}${rank}`;
              const isDarkSquare =
                (fileIndex(square) + rankIndex(square)) % 2 === 1;
              const isOrigin = square === selectedFrom;
              const isTarget = legalTargets.has(square);
              const highlighted = isOrigin || isTarget;
              const name =
                selectedFrom && isTarget
                  ? describeTarget(
                      fen,
                      selectedFrom,
                      square,
                      costFor(selectedFrom, square),
                      tA11y,
                    )
                  : describeSquare(fen, square, tA11y);
              const piece = pieceAt(fen, square);

              return (
                <div
                  key={square}
                  ref={(el) => {
                    cellRefs.current.set(square, el);
                  }}
                  role="gridcell"
                  aria-label={name}
                  aria-selected={highlighted ? true : undefined}
                  tabIndex={square === focused ? 0 : -1}
                  onFocus={() => setFocused(square)}
                  className={`flex aspect-square items-center justify-center text-xl outline-none ${
                    isDarkSquare
                      ? "bg-[var(--background)]"
                      : "bg-[var(--surface)]"
                  } ${highlighted ? "ring-2 ring-inset ring-[var(--bounty)]" : ""}`}
                >
                  {piece && (
                    <span aria-hidden="true">
                      {GLYPHS[piece.color][piece.type]}
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        ))}
      </div>

      {interactive && (
        <MoveEntryInput
          legalUci={legalUci}
          onMove={onMove}
          onAnnounce={onAnnounce}
        />
      )}
    </section>
  );
}
