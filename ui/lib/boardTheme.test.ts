import { describe, expect, it } from "vitest";
import {
  BOARD_DARK_SQUARE,
  BOARD_LIGHT_SQUARE,
  PIECE_BLACK,
  PIECE_WHITE,
  WCAG_NONTEXT_MIN,
  contrastRatio,
} from "./boardTheme";

/**
 * WCAG 2.2 SC 1.4.11: piece fills are graphical objects required to understand the board,
 * so each side must clear 3:1 against both square shades. The live SVG set and MiniBoard
 * tints share PIECE_WHITE / PIECE_BLACK, so one matrix covers both surfaces.
 */
describe("boardTheme contrast", () => {
  it("gives both piece colours 3:1 against both squares", () => {
    const pairs: [string, string, string][] = [
      ["white on light", PIECE_WHITE, BOARD_LIGHT_SQUARE],
      ["white on dark", PIECE_WHITE, BOARD_DARK_SQUARE],
      ["black on light", PIECE_BLACK, BOARD_LIGHT_SQUARE],
      ["black on dark", PIECE_BLACK, BOARD_DARK_SQUARE],
    ];
    for (const [label, piece, square] of pairs) {
      expect(contrastRatio(piece, square), label).toBeGreaterThanOrEqual(
        WCAG_NONTEXT_MIN,
      );
    }
  });
});
