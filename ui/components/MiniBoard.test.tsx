import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MiniBoard } from "./MiniBoard";
import { BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE } from "@/lib/boardTheme";

/** jsdom normalises inline colours to `rgb(...)`, so compare in that form. */
function toRgb(hex: string): string {
  const n = parseInt(hex.slice(1), 16);
  return `rgb(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255})`;
}

const START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

/** Squares are emitted a8..h8, a7..h7, … a1..h1, so this indexes the rendered cell list. */
function cellIndex(square: string): number {
  const file = square.charCodeAt(0) - "a".charCodeAt(0);
  const rank = Number(square[1]);
  return (8 - rank) * 8 + file;
}

function cells(container: HTMLElement): HTMLElement[] {
  const board = container.firstElementChild as HTMLElement;
  return Array.from(board.children) as HTMLElement[];
}

/** The `data-piece` hook (e.g. "wr", "bp") `PieceIcon` stamps on its wrapper, or null if empty. */
function pieceOf(square: HTMLElement): string | null {
  return square.querySelector<HTMLElement>("[data-piece]")?.dataset.piece ?? null;
}

describe("MiniBoard", () => {
  it("renders 64 squares", () => {
    const { container } = render(<MiniBoard fen={START_FEN} />);
    expect(cells(container)).toHaveLength(64);
  });

  it("places pieces on the squares the FEN names, using the shared piece icon set", () => {
    const { container } = render(<MiniBoard fen={START_FEN} />);
    const squares = cells(container);

    // Both back ranks are full, both middle ranks are empty.
    expect(pieceOf(squares[cellIndex("a8")])).toBe("br");
    expect(pieceOf(squares[cellIndex("d8")])).toBe("bq");
    expect(pieceOf(squares[cellIndex("e1")])).toBe("wk");
    expect(pieceOf(squares[cellIndex("g1")])).toBe("wn");
    expect(pieceOf(squares[cellIndex("e7")])).toBe("bp");
    expect(pieceOf(squares[cellIndex("e4")])).toBeNull();
  });

  it("gives the two colours of the same piece different icons", () => {
    const { container } = render(<MiniBoard fen={START_FEN} />);
    const squares = cells(container);

    // Same piece type on both sides, distinguished by colour. If these ever match, every piece on
    // the board reads as one side.
    expect(pieceOf(squares[cellIndex("a1")])).toBe("wr");
    expect(pieceOf(squares[cellIndex("a8")])).toBe("br");
    expect(pieceOf(squares[cellIndex("a1")])).not.toBe(pieceOf(squares[cellIndex("a8")]));
  });

  it("alternates square colours, and uses the same pair as the full board", () => {
    const { container } = render(<MiniBoard fen={START_FEN} />);
    const squares = cells(container);
    const colorOf = (square: string) =>
      squares[cellIndex(square)].style.backgroundColor;

    // a1 and h8 are the same colour; a1 and b1 are not. Getting this inverted is the classic
    // board-rendering bug and looks wrong to any player at a glance.
    expect(colorOf("a1")).toEqual(colorOf("h8"));
    expect(colorOf("a1")).not.toEqual(colorOf("b1"));

    // And both come from the shared pair, so the lobby board cannot drift from the game board.
    expect([BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE].map(toRgb)).toContain(
      colorOf("a1"),
    );
    expect([BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE].map(toRgb)).toContain(
      colorOf("b1"),
    );
  });

  it("renders a mid-game position without carrying over the start position", () => {
    const fen =
      "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4";
    const { container } = render(<MiniBoard fen={fen} />);
    const squares = cells(container);

    expect(pieceOf(squares[cellIndex("e4")])).toBe("wp");
    expect(pieceOf(squares[cellIndex("c4")])).toBe("wb");
    expect(pieceOf(squares[cellIndex("b1")])).toBe("wn");
    expect(pieceOf(squares[cellIndex("g1")])).toBeNull();
  });

  it("does not throw on a malformed FEN", () => {
    // The board is fed a server-published read-model, so a truncated or garbled string must degrade
    // to an empty board rather than take the whole /watch grid down with it.
    expect(() => render(<MiniBoard fen="not-a-fen" />)).not.toThrow();
    expect(() => render(<MiniBoard fen="" />)).not.toThrow();
  });

  it("is hidden from assistive technology", () => {
    // /watch is axe-scanned; the accessible name lives on the link wrapping this board.
    const { container } = render(<MiniBoard fen={START_FEN} />);
    expect(container.firstElementChild).toHaveAttribute("aria-hidden", "true");
  });
});
