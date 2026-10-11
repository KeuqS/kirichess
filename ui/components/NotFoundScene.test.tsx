import { readFileSync } from "node:fs";
import { join } from "node:path";
import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { NOT_FOUND_FEN, NotFoundScene } from "./NotFoundScene";

const SOURCE = readFileSync(join(__dirname, "NotFoundScene.tsx"), "utf8");

describe("NotFoundScene", () => {
  it("does not import react-chessboard (P-7-1)", () => {
    expect(SOURCE).not.toMatch(/from ["']react-chessboard["']/);
    expect(SOURCE).toMatch(/MiniBoard/);
  });

  it("is hidden from assistive technology", () => {
    const { container } = render(<NotFoundScene />);
    expect(container.firstElementChild).toHaveAttribute("aria-hidden", "true");
  });

  it("is the starting position minus the black knight that jumped to i9", () => {
    expect(NOT_FOUND_FEN.startsWith("rnbqkb1r/pppppppp/")).toBe(true);
    expect(NOT_FOUND_FEN).not.toContain("rnbqkbnr");
  });

  it("does not start the leap until the board has been on screen", () => {
    const { container } = render(<NotFoundScene />);
    expect(container.querySelector(".not-found-knight")).not.toHaveClass(
      "is-leaping",
    );
  });

  it("uses the live-board knight drawing, leaping from g8 toward i9", () => {
    const { container } = render(<NotFoundScene />);
    const knight = container.querySelector(".not-found-knight svg");
    expect(knight).not.toBeNull();
    expect(knight).toHaveAttribute("viewBox", "0 0 45 45");
    expect(container.textContent).toContain("i9?");
    expect(container.querySelector(".not-found-knight")).toHaveClass(
      "col-start-8",
      "row-start-2",
    );
  });
});
