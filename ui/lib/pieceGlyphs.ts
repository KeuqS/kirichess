// Unicode piece glyphs for AccessibleBoard, where the glyph is decorative anyway (the real
// content is the `aria-label` on each cell) - not a visual piece theme, so it stays text rather
// than the shared `PieceIcon` SVG set every other board draws from (Board.tsx, MiniBoard).

import type { Color, PieceChar } from "@/lib/fen";

/**
 * The literal Unicode pair: hollow glyphs for White, solid for Black. This is the set
 * `AccessibleBoard` renders, where the glyph is decorative anyway (the real content is the
 * `aria-label` on each cell) and matching the codepoint to the colour keeps the markup honest.
 */
export const GLYPHS: Record<Color, Record<PieceChar, string>> = {
  w: { p: "♙", n: "♘", b: "♗", r: "♖", q: "♕", k: "♔" },
  b: { p: "♟", n: "♞", b: "♝", r: "♜", q: "♛", k: "♚" },
};
