import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { AccessibleBoard } from "./AccessibleBoard";

const START_FEN =
  "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

function renderBoard(overrides: Partial<ComponentProps<typeof AccessibleBoard>> = {}) {
  const defaults: ComponentProps<typeof AccessibleBoard> = {
    fen: START_FEN,
    legalUci: ["e2e4"],
    sideToMove: "White",
    you: "White",
    onMove: vi.fn(),
    costFor: () => undefined,
    onAnnounce: vi.fn(),
  };
  const props = { ...defaults, ...overrides };
  const view = render(
    <NextIntlClientProvider locale="en" messages={en}>
      <AccessibleBoard {...props} />
    </NextIntlClientProvider>,
  );
  return {
    grid: screen.getByRole("grid"),
    rerender: (next: Partial<ComponentProps<typeof AccessibleBoard>>) => {
      view.rerender(
        <NextIntlClientProvider locale="en" messages={en}>
          <AccessibleBoard {...props} {...next} />
        </NextIntlClientProvider>,
      );
    },
    ...props,
  };
}

describe("AccessibleBoard roving tabindex (Phase 10b.3)", () => {
  it("starts focused on e1", () => {
    renderBoard();
    expect(screen.getByRole("gridcell", { name: /^e1,/ })).toHaveAttribute(
      "tabindex",
      "0",
    );
    expect(screen.getByRole("gridcell", { name: /^e2,/ })).toHaveAttribute(
      "tabindex",
      "-1",
    );
  });

  it("ArrowUp moves both the roving tabindex and DOM focus up one rank", () => {
    const { grid } = renderBoard();
    fireEvent.keyDown(grid, { key: "ArrowUp" });

    const e2 = screen.getByRole("gridcell", { name: /^e2,/ });
    expect(e2).toHaveAttribute("tabindex", "0");
    expect(document.activeElement).toBe(e2);
    expect(screen.getByRole("gridcell", { name: /^e1,/ })).toHaveAttribute(
      "tabindex",
      "-1",
    );
  });

  it("ArrowRight never wraps past file h", () => {
    const { grid } = renderBoard();
    for (let i = 0; i < 3; i++) fireEvent.keyDown(grid, { key: "ArrowRight" });
    expect(screen.getByRole("gridcell", { name: /^h1,/ })).toHaveAttribute(
      "tabindex",
      "0",
    );

    fireEvent.keyDown(grid, { key: "ArrowRight" });
    expect(screen.getByRole("gridcell", { name: /^h1,/ })).toHaveAttribute(
      "tabindex",
      "0",
    );
  });
});

describe("AccessibleBoard selection and move entry (Phase 10b.3)", () => {
  it("prices a legal target through costFor and plays the move on Enter", () => {
    const costFor = vi.fn((from: string, to: string) =>
      from === "e2" && to === "e4" ? -1 : undefined,
    );
    const { grid, onMove } = renderBoard({ legalUci: ["e2e4"], costFor });

    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e1 -> e2
    fireEvent.keyDown(grid, { key: "Enter" }); // select e2

    // costFor's -1 (mover pays one second) is folded into the target square's accessible name -
    // this is the consumer contract 10b.3 asks this suite to cover.
    expect(
      screen.getByRole("gridcell", { name: "e4, empty, costs you 1 second" }),
    ).toBeInTheDocument();

    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e2 -> e3
    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e3 -> e4
    fireEvent.keyDown(grid, { key: "Enter" }); // play e2e4

    expect(onMove).toHaveBeenCalledWith("e2e4");
  });

  it("announces an illegal target instead of failing silently", () => {
    const { grid, onAnnounce } = renderBoard({ legalUci: ["e2e4"] });

    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e1 -> e2
    fireEvent.keyDown(grid, { key: "Enter" }); // select e2
    expect(onAnnounce).toHaveBeenCalledWith("e2 selected, 1 legal target.");
    fireEvent.keyDown(grid, { key: "ArrowRight" }); // e2 -> f2, not a legal target
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onAnnounce).toHaveBeenCalledWith("No legal move from e2 to f2.");
  });

  it("Escape clears the selection and announces it", () => {
    const { grid, onAnnounce } = renderBoard();

    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e1 -> e2
    fireEvent.keyDown(grid, { key: "Enter" }); // select e2
    fireEvent.keyDown(grid, { key: "Escape" });

    expect(onAnnounce).toHaveBeenCalledWith("Selection cleared.");
  });

  it("interactive=false disables move entry and Enter no-ops", () => {
    const { grid, onMove } = renderBoard({ interactive: false });

    expect(screen.queryByLabelText("Move")).toBeNull();

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onMove).not.toHaveBeenCalled();
  });
});

describe("AccessibleBoard on-demand queries (Phase 10b.3)", () => {
  it("c, m and ? call the on-demand handlers rather than being swallowed", () => {
    const onRequestClocks = vi.fn();
    const onRequestLastMove = vi.fn();
    const onAnnounce = vi.fn();
    const { grid } = renderBoard({ onRequestClocks, onRequestLastMove, onAnnounce });

    fireEvent.keyDown(grid, { key: "c" });
    fireEvent.keyDown(grid, { key: "m" });
    fireEvent.keyDown(grid, { key: "?" });

    expect(onRequestClocks).toHaveBeenCalledTimes(1);
    expect(onRequestLastMove).toHaveBeenCalledTimes(1);
    expect(onAnnounce).toHaveBeenCalledWith(
      expect.stringContaining("Arrow keys move the cursor"),
    );
  });

  it("surfaces C and M in the visible panel help, not only in the ? summary", () => {
    renderBoard();
    expect(
      screen.getByText(/C for clocks, M for last move/),
    ).toBeInTheDocument();
  });
});

describe("AccessibleBoard selection announcement (P-5-4)", () => {
  it("announces the square and legal-target count on Enter", () => {
    const { grid, onAnnounce } = renderBoard({
      legalUci: ["e2e3", "e2e4"],
    });

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onAnnounce).toHaveBeenCalledWith("e2 selected, 2 legal targets.");
  });

  it("announces Selection cleared when re-activating the origin", () => {
    const { grid, onAnnounce } = renderBoard({ legalUci: ["e2e4"] });

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onAnnounce).toHaveBeenCalledWith("Selection cleared.");
  });
});

describe("AccessibleBoard promotion prompt (P-5-6)", () => {
  const PROMOTION_FEN = "4k3/P7/8/8/8/8/8/4K3 w - - 0 1";
  const ALL_PROMOTIONS = ["a7a8q", "a7a8n", "a7a8b", "a7a8r"];

  function selectPawnAndTargetA8(
    grid: HTMLElement,
    onAnnounce: ReturnType<typeof vi.fn>,
  ) {
    fireEvent.keyDown(grid, { key: "Home" });
    for (let i = 0; i < 6; i++) fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });
    expect(onAnnounce).toHaveBeenCalledWith("a7 selected, 1 legal target.");
    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });
  }

  it("prompts for a piece when more than one promotion is legal, then plays N", () => {
    const onMove = vi.fn();
    const onAnnounce = vi.fn();
    const { grid } = renderBoard({
      fen: PROMOTION_FEN,
      legalUci: ALL_PROMOTIONS,
      onMove,
      onAnnounce,
    });

    selectPawnAndTargetA8(grid, onAnnounce);

    expect(onAnnounce).toHaveBeenCalledWith(
      "Promote on a8: press Q for queen, N for knight, B for bishop, or R for rook.",
    );
    expect(onMove).not.toHaveBeenCalled();

    fireEvent.keyDown(grid, { key: "n" });
    expect(onMove).toHaveBeenCalledWith("a7a8n");
  });

  it("Enter during the prompt defaults to queen", () => {
    const onMove = vi.fn();
    const onAnnounce = vi.fn();
    const { grid } = renderBoard({
      fen: PROMOTION_FEN,
      legalUci: ALL_PROMOTIONS,
      onMove,
      onAnnounce,
    });

    selectPawnAndTargetA8(grid, onAnnounce);
    fireEvent.keyDown(grid, { key: "Enter" });
    expect(onMove).toHaveBeenCalledWith("a7a8q");
  });

  it("plays immediately when only one promotion piece is legal", () => {
    const onMove = vi.fn();
    const onAnnounce = vi.fn();
    const { grid } = renderBoard({
      fen: PROMOTION_FEN,
      legalUci: ["a7a8n"],
      onMove,
      onAnnounce,
    });

    selectPawnAndTargetA8(grid, onAnnounce);
    expect(onMove).toHaveBeenCalledWith("a7a8n");
    expect(onAnnounce).not.toHaveBeenCalledWith(
      expect.stringContaining("Promote on"),
    );
  });
});

describe("AccessibleBoard preselection while the opponent thinks", () => {
  const E2_CAPTURED =
    "rnbqkbnr/pppppppp/8/8/8/8/PPPP1PPP/RNBQKBNR w KQkq - 0 1";

  function cell(name: RegExp) {
    return screen.getByRole("gridcell", { name });
  }

  it("holds your piece, shows its destinations, and does not play", () => {
    const { grid, onMove, onAnnounce } = renderBoard({
      fen: START_FEN,
      legalUci: ["e7e5", "e7e6"],
      sideToMove: "Black",
      you: "White",
    });

    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e1 -> e2
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onAnnounce).toHaveBeenCalledWith("e2 selected, 2 legal targets.");
    expect(onMove).not.toHaveBeenCalled();
    expect(cell(/^e2,/)).toHaveAttribute("aria-selected", "true");
    expect(cell(/^e4,/)).toHaveAttribute("aria-selected", "true");
  });

  it("does not select an opponent piece that has a legal move", () => {
    const { grid, onAnnounce, onMove } = renderBoard({
      fen: START_FEN,
      legalUci: ["e7e5"],
      sideToMove: "Black",
      you: "White",
    });

    for (let i = 0; i < 6; i++) fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onAnnounce).not.toHaveBeenCalled();
    expect(onMove).not.toHaveBeenCalled();
    expect(cell(/^e7,/)).not.toHaveAttribute("aria-selected");
  });

  it("clears a held piece when Enter lands on an empty square, and does not play", () => {
    const { grid, onMove, onAnnounce } = renderBoard({
      fen: START_FEN,
      legalUci: ["e2e4", "e7e5"],
      sideToMove: "Black",
      you: "White",
    });

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });
    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e2 -> e3
    fireEvent.keyDown(grid, { key: "ArrowUp" }); // e3 -> e4
    fireEvent.keyDown(grid, { key: "Enter" });

    expect(onMove).not.toHaveBeenCalled();
    expect(onAnnounce).toHaveBeenCalledWith("Selection cleared.");
    expect(cell(/^e2,/)).not.toHaveAttribute("aria-selected");
  });

  it("shows legal targets once it is your turn, then plays the move", () => {
    const { grid, onMove, rerender } = renderBoard({
      fen: START_FEN,
      legalUci: ["e7e5"],
      sideToMove: "Black",
      you: "White",
    });

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });

    rerender({
      sideToMove: "White",
      legalUci: ["e2e4"],
    });

    expect(cell(/^e4,/)).toHaveAttribute("aria-selected", "true");

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });
    expect(onMove).toHaveBeenCalledWith("e2e4");
  });

  it("announces that the selection is gone when the held piece is captured", () => {
    const { grid, onAnnounce, rerender } = renderBoard({
      fen: START_FEN,
      legalUci: ["e7e5"],
      sideToMove: "Black",
      you: "White",
    });

    fireEvent.keyDown(grid, { key: "ArrowUp" });
    fireEvent.keyDown(grid, { key: "Enter" });

    rerender({ fen: E2_CAPTURED });

    expect(onAnnounce).toHaveBeenCalledWith("Selection cleared.");
    expect(cell(/^e2,/)).not.toHaveAttribute("aria-selected");
  });
});
