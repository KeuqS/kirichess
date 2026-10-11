/**
 * Square colours, shared by every board that draws one.
 *
 * These are deliberately *not* `--background` / `--surface`. Those two are both
 * near-black in this theme, so a board built from them barely reads as a checkerboard and, worse,
 * swallows the black pieces entirely. A board needs its own two-tone pair with real contrast
 * against both piece colours, which is what these are.
 *
 * Shared rather than repeated so the lobby's `MiniBoard` cannot drift from the `Board` a player
 * sees once they click through: two boards on two screens in two different palettes is exactly the
 * kind of mismatch nobody notices in review and everybody notices in use.
 *
 * Atom One Dark, same family as the chrome. Dark squares are the comment grey (`#5c6370`).
 * Light squares sit on the comment→muted ramp, darkened until white fill clears WCAG 2.2
 * 1.4.11's 3:1 non-text floor (`--muted` itself is 2.93:1 against `#ffffff` and fails).
 * Piece fills match the Wikipedia / Cburnett set `react-chessboard` ships (`#ffffff` /
 * `#000000`) so MiniBoard and the live board are measured against the same pair.
 *
 * Checker contrast between the two squares cannot also be 3:1: near-white and near-black
 * pieces that each clear 3:1 on both shades leave no room for the squares to clear 3:1
 * with each other. 1.4.11 here is the pieces.
 */
export const BOARD_DARK_SQUARE = "#5c6370";
export const BOARD_LIGHT_SQUARE = "#8c929e";

/**
 * The live board's piece fills (`PieceIcon`, `react-chessboard`'s own defaults). Watch-card side
 * chips (`LiveGameCard`'s `SideSwatch`) use the same pair so "this player is White" cannot be
 * mistaken for a board square.
 */
export const PIECE_WHITE = "#ffffff";
export const PIECE_BLACK = "#000000";

/** WCAG 2.2 SC 1.4.11 non-text contrast floor (graphical objects). */
export const WCAG_NONTEXT_MIN = 3;

/**
 * WCAG 2.2 relative luminance of an `#rrggbb` sRGB colour.
 * https://www.w3.org/TR/WCAG22/#dfn-relative-luminance
 */
function luminance(hex: string): number {
  const n = hex.replace("#", "");
  const rgb = [0, 2, 4].map((i) => {
    const c = parseInt(n.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
}

/**
 * WCAG contrast ratio of two `#rrggbb` colours.
 * https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio
 */
export function contrastRatio(a: string, b: string): number {
  const l1 = luminance(a);
  const l2 = luminance(b);
  const hi = Math.max(l1, l2);
  const lo = Math.min(l1, l2);
  return (hi + 0.05) / (lo + 0.05);
}
