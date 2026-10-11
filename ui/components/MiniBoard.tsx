import { parseBoard, type Square } from "@/lib/fen";
import { PieceIcon } from "@/components/PieceIcon";
import { BOARD_DARK_SQUARE, BOARD_LIGHT_SQUARE } from "@/lib/boardTheme";

interface MiniBoardProps {
  /** Position to draw. Always drawn from White's point of view, like every lobby board. */
  fen: string;
  className?: string;
}

const RANKS = [8, 7, 6, 5, 4, 3, 2, 1];
const FILES = ["a", "b", "c", "d", "e", "f", "g", "h"];

/**
 * A static, non-interactive board for the live-games list.
 *
 * Deliberately not `Board.tsx`: that one is react-chessboard plus react-dnd-touch-backend, with
 * drag handlers and memoised square styles, which is a lot of machinery to mount a dozen times on a
 * page where nothing is draggable. This renders 64 divs and no state.
 *
 * `aria-hidden` on purpose. /watch is one of the axe-scanned anonymous pages
 * (e2e/tests/accessibility.spec.ts), and a dozen unlabelled 64-cell grids would be noise to a
 * screen-reader user and slow to scan for no gain: the accessible name of a game lives on the link
 * wrapping this board, and the playable board is `AccessibleBoard` on /watch/[id].
 */
export function MiniBoard({ fen, className = "" }: MiniBoardProps) {
  // Parsed once per board. `pieceAt` would be the obvious reach, but it reparses the whole FEN on
  // every call, so a per-square loop over twelve boards is 768 parses per render instead of 12.
  const board = parseBoard(fen);

  return (
    <div
      aria-hidden="true"
      className={`grid aspect-square w-full grid-cols-8 auto-rows-fr overflow-hidden border border-[var(--border)] ${className}`}
    >
      {RANKS.map((rank, rankIdx) =>
        FILES.map((file, fileIdx) => {
          const square: Square = `${file}${rank}`;
          const piece = board[square];
          const isDark = (fileIdx + rankIdx) % 2 === 1;

          return (
            <div
              key={square}
              className="flex items-center justify-center"
              style={{
                backgroundColor: isDark
                  ? BOARD_DARK_SQUARE
                  : BOARD_LIGHT_SQUARE,
              }}
            >
              {piece && (
                <PieceIcon
                  type={piece.type}
                  color={piece.color}
                  className="h-[85%] w-[85%]"
                />
              )}
            </div>
          );
        }),
      )}
    </div>
  );
}
