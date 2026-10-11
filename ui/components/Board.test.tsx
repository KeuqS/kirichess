import { act, fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE } from "@/lib/boardTheme";

// P-5-7: Board.tsx renders the real <Chessboard>, which drags in react-dnd's DOM backend and is
// heavier than this file needs for one prop-gating assertion. Mocking the module lets the test
// capture exactly the props Board.tsx passes through, which is the thing under test.
let lastChessboardProps: Record<string, unknown> | null = null;
vi.mock("react-chessboard", () => ({
  Chessboard: (props: Record<string, unknown>) => {
    lastChessboardProps = props;
    return null;
  },
}));

import { Board, squareStylesForSelection } from "./Board";

/** Narrows the captured mock props to the handful this file drives directly. */
function chessboardProps() {
  return lastChessboardProps as {
    onSquareClick?: (square: string) => void;
    onPieceClick?: (piece: string, square: string) => void;
    onPieceDrop?: (source: string, target: string, piece: string) => boolean;
    onPieceDragBegin?: (piece: string, square: string) => void;
    onPieceDragEnd?: (piece: string, square: string) => void;
    isDraggablePiece?: (arg: { piece: string }) => boolean;
    customSquareStyles?: Record<
      string,
      { opacity?: number; boxShadow?: string; backgroundImage?: string }
    >;
  } | null;
}

function mockPrefersReducedMotion(matches: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  })) as unknown as typeof window.matchMedia;
}

const START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
/** 1.e4 d5 - White's e-pawn can push or capture. */
const E4_D5_FEN =
  "rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2";

describe("squareStylesForSelection", () => {
  it("returns nothing until a piece is selected", () => {
    expect(squareStylesForSelection(START_FEN, ["e2e4"], null)).toEqual({});
  });

  it("marks the origin with the amber inset, not a dest hint", () => {
    const styles = squareStylesForSelection(START_FEN, ["e2e3", "e2e4"], "e2");
    expect(styles.e2?.boxShadow).toMatch(/inset/);
    expect(styles.e2?.backgroundImage).toBeUndefined();
    expect(styles.e2?.backgroundColor).toBeUndefined();
  });

  it("paints a centered dot on empty dests instead of darkening the whole square", () => {
    const styles = squareStylesForSelection(START_FEN, ["e2e3", "e2e4"], "e2");
    expect(styles.e4?.backgroundColor).toBeUndefined();
    expect(styles.e4?.backgroundImage).toMatch(/radial-gradient/);
    expect(styles.e4?.backgroundImage).toMatch(/var\(--hue-green\) 66%/);
    expect(styles.e4?.backgroundImage).toMatch(/22%/);
    expect(styles.e3?.backgroundImage).toEqual(styles.e4?.backgroundImage);
  });

  it("paints a capture ring on occupied dests so the piece stays visible", () => {
    const styles = squareStylesForSelection(E4_D5_FEN, ["e4e5", "e4d5"], "e4");
    expect(styles.e5?.backgroundImage).toMatch(/22%/);
    expect(styles.d5?.backgroundImage).toMatch(/transparent 79%/);
    expect(styles.d5?.backgroundImage).toMatch(/var\(--hue-green\)/);
    expect(styles.d5?.backgroundColor).toBeUndefined();
  });

  it("tints the last ply's from and to squares, including promotions", () => {
    const styles = squareStylesForSelection(START_FEN, [], null, "e7e8q");
    expect(styles.e7?.backgroundImage).toMatch(/var\(--bounty\)/);
    expect(styles.e8?.backgroundImage).toEqual(styles.e7?.backgroundImage);
    expect(styles.e7?.backgroundColor).toBeUndefined();
    expect(styles.d2).toBeUndefined();
  });

  it("C-11-7: dims a pending square without touching piece position (no colour, just opacity)", () => {
    const styles = squareStylesForSelection(START_FEN, [], null, null, "e2");
    expect(styles.e2?.opacity).toBe(0.5);
    expect(styles.e2?.backgroundImage).toBeUndefined();
    expect(styles.e2?.backgroundColor).toBeUndefined();
    expect(styles.e4).toBeUndefined();
  });

  it("C-11-7: a pending square keeps its last-move tint underneath the dim", () => {
    // The from-square of the move just submitted is also the to-square of the previous ply in
    // some sequences; the dim must not clobber the tint already computed for it.
    const styles = squareStylesForSelection(START_FEN, [], null, "d2e3", "e3");
    expect(styles.e3?.backgroundImage).toMatch(/var\(--bounty\)/);
    expect(styles.e3?.opacity).toBe(0.5);
  });

  it("keeps dest hints on top of a last-move square they share", () => {
    const styles = squareStylesForSelection(
      E4_D5_FEN,
      ["e4e5", "e4d5"],
      "e4",
      "d7d5",
    );
    expect(styles.d5?.backgroundImage).toMatch(/^radial-gradient/);
    expect(styles.d5?.backgroundImage).toMatch(/var\(--bounty\)/);
    expect(styles.d7?.backgroundImage).toMatch(/var\(--bounty\)/);
    expect(styles.d7?.backgroundImage).not.toMatch(/radial-gradient/);
  });
});

describe("Board reduced-motion animation gating (P-5-7)", () => {
  afterEach(() => {
    lastChessboardProps = null;
  });

  function renderBoard() {
    render(
      <Board
        fen={START_FEN}
        legalUci={["e2e4"]}
        sideToMove="White"
        onMove={() => {}}
        costFor={() => undefined}
      />,
    );
  }

  it("passes the default 300ms duration when reduced motion is not requested", () => {
    mockPrefersReducedMotion(false);
    renderBoard();
    expect(lastChessboardProps?.animationDuration).toBe(300);
  });

  it("passes a 0ms duration when the user prefers reduced motion", () => {
    mockPrefersReducedMotion(true);
    renderBoard();
    expect(lastChessboardProps?.animationDuration).toBe(0);
  });
});

describe("Board customBoardStyle (P-10-2)", () => {
  afterEach(() => {
    lastChessboardProps = null;
  });

  it("does not pass a dead borderRadius or an rgba drop shadow", () => {
    mockPrefersReducedMotion(false);
    render(
      <Board
        fen={START_FEN}
        legalUci={[]}
        sideToMove="White"
        onMove={() => {}}
        costFor={() => undefined}
      />,
    );
    expect(lastChessboardProps?.customBoardStyle).toBeUndefined();
  });
});

describe("Board viewport cap (P-6-2)", () => {
  afterEach(() => {
    lastChessboardProps = null;
  });

  it("wraps the chessboard in the height-capped class, not a width-only max-w-[800px]", () => {
    mockPrefersReducedMotion(false);
    const { container } = render(
      <Board
        fen={START_FEN}
        legalUci={[]}
        sideToMove="White"
        onMove={() => {}}
        costFor={() => undefined}
      />,
    );
    expect(container.firstElementChild).toHaveClass("board-viewport-cap");
    expect(container.firstElementChild?.className).not.toMatch(
      /max-w-\[800px\]/,
    );
  });
});

describe("Board pending affordance on click moves (C-11-7)", () => {
  afterEach(() => {
    lastChessboardProps = null;
  });

  function renderBoard(onMove: (uci: string) => void | Promise<unknown>) {
    mockPrefersReducedMotion(false);
    render(
      <Board
        fen={START_FEN}
        legalUci={["e2e4"]}
        sideToMove="White"
        onMove={onMove}
        costFor={() => undefined}
      />,
    );
  }

  function clickSquare(square: string) {
    act(() => {
      chessboardProps()?.onSquareClick?.(square);
    });
  }

  /**
   * A controllable stand-in for the in-flight `SubmitMove` promise `handleMove` returns. Using
   * definite-assignment (`!`) rather than a nullable `let` reassigned inside the executor keeps
   * `resolve`/`reject` at their real, non-null types (no `?.()` call needed) - the nullable form
   * hit a TS control-flow quirk (narrowed to `never` at the synchronous call site) once one of
   * these tests read the variable outside an `async` closure.
   */
  function deferredMove(): {
    promise: Promise<void>;
    resolve: () => void;
    reject: (err: unknown) => void;
  } {
    let resolve!: () => void;
    let reject!: (err: unknown) => void;
    const promise = new Promise<void>((res, rej) => {
      resolve = res;
      reject = rej;
    });
    return { promise, resolve, reject };
  }

  it("does nothing extra for a plain void-returning handler (the watch/replay/learn boards' shape)", () => {
    const onMove = vi.fn();
    renderBoard(onMove);
    clickSquare("e2");
    clickSquare("e4");

    expect(onMove).toHaveBeenCalledWith("e2e4");
    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBeUndefined();
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(true);
  });

  it("dims the origin square and blocks further input while a click move is in flight", () => {
    const { promise, resolve } = deferredMove();
    const onMove = vi.fn(() => promise);
    renderBoard(onMove);
    clickSquare("e2");
    clickSquare("e4");

    expect(onMove).toHaveBeenCalledTimes(1);
    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBe(0.5);
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(false);

    // A further click while pending is swallowed, not routed into a second onMove: two clicks
    // racing the still-stale legalUci is exactly the hazard the pending gate exists to close.
    clickSquare("d2");
    expect(onMove).toHaveBeenCalledTimes(1);

    // Let the in-flight promise settle so it doesn't leak into the next test.
    resolve();
  });

  it("locks the board on a drag the same way a click does, including a same-gesture click", () => {
    const { promise, resolve } = deferredMove();
    const onMove = vi.fn(() => promise);
    mockPrefersReducedMotion(false);
    render(
      <Board
        fen={START_FEN}
        legalUci={["e2e4", "d2d4"]}
        sideToMove="White"
        onMove={onMove}
        costFor={() => undefined}
      />,
    );

    let dropped = false;
    act(() => {
      chessboardProps()?.onPieceDragBegin?.("wP", "e2");
      dropped = chessboardProps()?.onPieceDrop?.("e2", "e4", "wP") ?? false;
      // react-chessboard also clicks the square a piece was dropped on, in the same turn,
      // before the pending state update is visible. A second drop from the still-stale
      // legal list must not submit either.
      chessboardProps()?.onSquareClick?.("e4");
      chessboardProps()?.onPieceDrop?.("d2", "d4", "wP");
    });

    expect(dropped).toBe(true);
    expect(onMove).toHaveBeenCalledTimes(1);
    expect(onMove).toHaveBeenCalledWith("e2e4");
    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBe(0.5);
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(false);

    resolve();
  });

  it("clears the pending square once the handler's promise resolves (the success exit)", async () => {
    const { promise, resolve } = deferredMove();
    const onMove = vi.fn(() => promise);
    renderBoard(onMove);
    clickSquare("e2");
    clickSquare("e4");
    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBe(0.5);

    await act(async () => {
      resolve();
      await promise;
    });

    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBeUndefined();
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(true);
  });

  it("clears the pending square even when the handler's promise rejects (the error exit)", async () => {
    const { promise, reject } = deferredMove();
    const onMove = vi.fn(() => promise);
    renderBoard(onMove);
    clickSquare("e2");
    clickSquare("e4");
    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBe(0.5);

    await act(async () => {
      // game/[id]/page.tsx's real handleMove always resolves (it catches SubmitMove's own
      // rejection internally and sets an error banner instead of rethrowing), but Board.tsx
      // must not assume every caller does the same: a bare `.finally()` would leave this
      // rejection unhandled, which is why Board attaches `.then(clear, clear)` instead.
      reject(new Error("submit failed"));
      await promise.catch(() => {});
    });

    expect(chessboardProps()?.customSquareStyles?.e2?.opacity).toBeUndefined();
  });

  it("never touches state after unmount, even if the in-flight promise settles later", async () => {
    const { promise, resolve } = deferredMove();
    const onMove = vi.fn(() => promise);
    mockPrefersReducedMotion(false);
    const { unmount } = render(
      <Board
        fen={START_FEN}
        legalUci={["e2e4"]}
        sideToMove="White"
        onMove={onMove}
        costFor={() => undefined}
      />,
    );
    clickSquare("e2");
    clickSquare("e4");
    expect(onMove).toHaveBeenCalledWith("e2e4");

    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    unmount();
    // If this throws, the test itself fails - that is the assertion. React 19 silently drops a
    // state update on an unmounted fiber rather than warning the way React 17/18 did, so this
    // can't by itself distinguish a present `mountedRef` guard from an absent one; the guard's
    // actual job is proven by the success/error tests above (`clear()` running and doing its
    // job while mounted). This test instead guards the outcome that matters here specifically:
    // letting the promise settle after unmount must be inert, not a crash or a console error.
    await act(async () => {
      resolve();
      await promise;
    });

    expect(errorSpy).not.toHaveBeenCalled();
    errorSpy.mockRestore();
  });
});

describe("Board preselection while the opponent thinks", () => {
  /** e2 is empty: the pawn that was held there has left the square. */
  const E2_CAPTURED =
    "rnbqkbnr/pppppppp/8/8/8/8/PPPP1PPP/RNBQKBNR w KQkq - 0 1";

  afterEach(() => {
    lastChessboardProps = null;
  });

  function renderLive(
    overrides: Partial<{
      fen: string;
      legalUci: string[];
      sideToMove: "White" | "Black";
      mySide: "White" | "Black" | undefined;
    }> = {},
  ) {
    mockPrefersReducedMotion(false);
    const onMove = vi.fn();
    const view = render(
      <Board
        fen={START_FEN}
        legalUci={["e7e5", "e7e6"]}
        sideToMove="Black"
        onMove={onMove}
        costFor={() => undefined}
        mySide="White"
        {...overrides}
      />,
    );
    return { onMove, ...view };
  }

  function click(square: string) {
    act(() => {
      chessboardProps()?.onSquareClick?.(square);
    });
  }

  function styles() {
    return chessboardProps()?.customSquareStyles;
  }

  it("highlights your own piece and shows its destinations without playing", () => {
    const { onMove } = renderLive();
    click("e2");

    expect(styles()?.e2?.boxShadow).toMatch(/inset/);
    expect(styles()?.e3?.backgroundImage).toMatch(/radial-gradient/);
    expect(styles()?.e4?.backgroundImage).toMatch(/radial-gradient/);
    expect(onMove).not.toHaveBeenCalled();
    // Own pieces stay draggable so a click on the piece reaches the board; the opponent's do not.
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(true);
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "bP" })).toBe(false);
  });

  it("highlights when the click lands on the piece itself, and a drop does not play", () => {
    const { onMove } = renderLive();
    act(() => {
      chessboardProps()?.onPieceClick?.("wP", "e2");
    });
    expect(styles()?.e2?.boxShadow).toMatch(/inset/);
    expect(styles()?.e4?.backgroundImage).toMatch(/radial-gradient/);

    act(() => {
      chessboardProps()?.onPieceDragBegin?.("wP", "d2");
    });
    let dropped = true;
    act(() => {
      dropped = chessboardProps()?.onPieceDrop?.("d2", "d4", "wP") ?? true;
      chessboardProps()?.onPieceDragEnd?.("wP", "d2");
    });
    expect(dropped).toBe(false);
    expect(onMove).not.toHaveBeenCalled();
    expect(styles()?.d2?.boxShadow).toMatch(/inset/);
  });

  it("does not treat a second click as a move, even if that uci is in the legal list", () => {
    const { onMove } = renderLive({ legalUci: ["e2e4", "e7e5"] });
    click("e2");
    click("e4");

    expect(onMove).not.toHaveBeenCalled();
    expect(styles()?.e2?.boxShadow).toBeUndefined();
  });

  it("moves the highlight to another of your pieces, and a second click on the same square clears it", () => {
    renderLive();
    click("e2");
    click("d2");
    expect(styles()?.e2?.boxShadow).toBeUndefined();
    expect(styles()?.d2?.boxShadow).toMatch(/inset/);

    click("d2");
    expect(styles()?.d2?.boxShadow).toBeUndefined();
  });

  it("ignores the opponent's pieces and empty squares when nothing is held", () => {
    renderLive();
    click("e7");
    click("e4");
    expect(styles()?.e7?.boxShadow).toBeUndefined();
    expect(styles()?.e4?.boxShadow).toBeUndefined();
  });

  it("keeps the highlight and shows destinations once it is your turn, then plays the click", () => {
    const { onMove, rerender } = renderLive();
    click("e2");

    rerender(
      <Board
        fen={START_FEN}
        legalUci={["e2e3", "e2e4"]}
        sideToMove="White"
        onMove={onMove}
        costFor={() => undefined}
        mySide="White"
      />,
    );

    expect(styles()?.e2?.boxShadow).toMatch(/inset/);
    expect(styles()?.e4?.backgroundImage).toMatch(/radial-gradient/);
    expect(chessboardProps()?.isDraggablePiece?.({ piece: "wP" })).toBe(true);

    click("e4");
    expect(onMove).toHaveBeenCalledWith("e2e4");
  });

  it("drops the highlight when the held piece is no longer on that square", () => {
    const { onMove, rerender } = renderLive();
    click("e2");

    rerender(
      <Board
        fen={E2_CAPTURED}
        legalUci={["e7e5"]}
        sideToMove="Black"
        onMove={onMove}
        costFor={() => undefined}
        mySide="White"
      />,
    );

    expect(styles()?.e2?.boxShadow).toBeUndefined();
  });

  it("does not preselect when there is no local player", () => {
    renderLive({ mySide: undefined });
    click("e2");
    expect(styles()?.e2?.boxShadow).toBeUndefined();
  });
});

describe("Board click-path promotion picker (C-11-6)", () => {
  const WHITE_PROMOTE = "8/P6k/8/8/8/8/8/4K3 w - - 0 1";
  const BLACK_PROMOTE = "4k3/8/8/8/8/8/p6K/8 b - - 0 1";
  const ALL = ["a7a8q", "a7a8r", "a7a8b", "a7a8n"];

  afterEach(() => {
    lastChessboardProps = null;
  });

  function renderPromoting(
    overrides: Partial<{
      fen: string;
      legalUci: string[];
      sideToMove: "White" | "Black";
      mySide: "White" | "Black";
      onMove: (uci: string) => void;
    }> = {},
  ) {
    mockPrefersReducedMotion(false);
    const onMove = overrides.onMove ?? vi.fn();
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <Board
          fen={overrides.fen ?? WHITE_PROMOTE}
          legalUci={overrides.legalUci ?? ALL}
          sideToMove={overrides.sideToMove ?? "White"}
          onMove={onMove}
          costFor={() => undefined}
          mySide={overrides.mySide}
        />
      </NextIntlClientProvider>,
    );
    return onMove;
  }

  function click(square: string) {
    act(() => {
      chessboardProps()?.onSquareClick?.(square);
    });
  }

  it("asks which piece, then plays the one that was chosen", () => {
    const onMove = renderPromoting();
    click("a7");
    click("a8");

    expect(onMove).not.toHaveBeenCalled();
    const dialog = screen.getByRole("dialog", { name: "Promote on a8." });
    expect(dialog).toHaveClass("flex-col");
    expect(dialog).not.toHaveClass("flex-col-reverse");
    expect(dialog.style.left).toBe("0%");
    expect(dialog.style.top).toBe("0px");

    const buttons = screen.getAllByRole("button");
    expect(buttons.map((button) => button.getAttribute("aria-label"))).toEqual([
      "queen",
      "rook",
      "bishop",
      "knight",
    ]);
    expect(buttons[0]).toHaveStyle({ backgroundColor: BOARD_LIGHT_SQUARE });
    expect(buttons[1]).toHaveStyle({ backgroundColor: BOARD_DARK_SQUARE });
    expect(buttons[0]).toHaveFocus();

    fireEvent.click(screen.getByRole("button", { name: "knight" }));
    expect(onMove).toHaveBeenCalledTimes(1);
    expect(onMove).toHaveBeenCalledWith("a7a8n");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("offers only the promotions the position allows", () => {
    renderPromoting({ legalUci: ["a7a8r", "a7a8n"] });
    click("a7");
    click("a8");

    expect(screen.getByRole("button", { name: "rook" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "knight" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "queen" })).toBeNull();
    expect(screen.queryByRole("button", { name: "bishop" })).toBeNull();
  });

  it("plays immediately when only one promotion is legal", () => {
    const onMove = renderPromoting({ legalUci: ["a7a8n"] });
    click("a7");
    click("a8");

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onMove).toHaveBeenCalledWith("a7a8n");
  });

  it("cancels on Escape and on a click outside the pieces, and keeps the pawn selected", () => {
    const onMove = renderPromoting();
    click("a7");
    click("a8");

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onMove).not.toHaveBeenCalled();
    expect(chessboardProps()?.customSquareStyles?.a7?.boxShadow).toMatch(/inset/);

    click("a8");
    const dialog = screen.getByRole("dialog");
    fireEvent.mouseDown(dialog.parentElement as HTMLElement);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onMove).not.toHaveBeenCalled();
  });

  it("does not open on a click that arrives while a drag is in progress", () => {
    const onMove = renderPromoting();
    act(() => {
      chessboardProps()?.onPieceDragBegin?.("wP", "a7");
      chessboardProps()?.onSquareClick?.("a8");
    });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onMove).not.toHaveBeenCalled();

    act(() => {
      chessboardProps()?.onPieceDragEnd?.("wP", "a7");
    });
    let dropped = false;
    act(() => {
      dropped =
        chessboardProps()?.onPieceDrop?.("a7", "a8", "wN") ?? false;
    });
    expect(dropped).toBe(true);
    expect(onMove).toHaveBeenCalledWith("a7a8n");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("grows down from the top edge when black is at the bottom of the board", () => {
    renderPromoting({
      fen: BLACK_PROMOTE,
      legalUci: ["a2a1q", "a2a1r", "a2a1b", "a2a1n"],
      sideToMove: "Black",
      mySide: "Black",
    });
    click("a2");
    click("a1");

    const dialog = screen.getByRole("dialog", { name: "Promote on a1." });
    expect(dialog).toHaveClass("flex-col");
    expect(dialog).not.toHaveClass("flex-col-reverse");
    // a-file is on the right when Black sits at the bottom.
    expect(dialog.style.left).toBe("87.5%");
    expect(
      screen.getByRole("button", { name: "queen" }).querySelector("[data-piece]"),
    ).toHaveAttribute("data-piece", "bq");
  });
});
