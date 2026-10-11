import { readFileSync } from "node:fs";
import { join } from "node:path";
import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { FORBIDDEN_FEN, ForbiddenScene } from "./ForbiddenScene";

const SOURCE = readFileSync(join(__dirname, "ForbiddenScene.tsx"), "utf8");

describe("ForbiddenScene", () => {
  it("does not import react-chessboard (P-7-1)", () => {
    expect(SOURCE).not.toMatch(/from ["']react-chessboard["']/);
    expect(SOURCE).toMatch(/MiniBoard/);
  });

  it("is hidden from assistive technology", () => {
    const { container } = render(<ForbiddenScene />);
    expect(container.firstElementChild).toHaveAttribute("aria-hidden", "true");
  });

  it("is the starting position minus both d-pawns, so d7 is empty for the overlay", () => {
    expect(FORBIDDEN_FEN.startsWith("rnbqkbnr/ppp1pppp/")).toBe(true);
    expect(FORBIDDEN_FEN).toContain("/PPP1PPPP/");
    expect(FORBIDDEN_FEN).toContain("k");
  });

  it("does not start the lunge until the board has been on screen", () => {
    const { container } = render(<ForbiddenScene />);
    expect(container.querySelector(".forbidden-pawn")).not.toHaveClass(
      "is-lunging",
    );
  });

  it("uses the live-board pawn drawing, sitting on d7 toward the king on e8", () => {
    const { container } = render(<ForbiddenScene />);
    const pawn = container.querySelector(".forbidden-pawn svg");
    expect(pawn).not.toBeNull();
    expect(pawn).toHaveAttribute("viewBox", "0 0 45 45");
    expect(container.querySelector(".forbidden-pawn")).toHaveClass(
      "col-start-5",
      "row-start-3",
    );
  });
});
