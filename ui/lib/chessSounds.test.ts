import { readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  SOUND_SRC,
  soundForMove,
  soundForReplayStep,
  type ChessSound,
} from "./chessSounds";

const soundsDir = join(__dirname, "../public/sounds");
const shippedCues = Object.keys(SOUND_SRC) as ChessSound[];

describe("soundForMove", () => {
  it("plays move for a quiet pawn push", () => {
    expect(
      soundForMove(
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "e2e4",
      ),
    ).toBe("move");
  });

  it("plays capture for a capture", () => {
    expect(
      soundForMove("4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1", "e4d5"),
    ).toBe("capture");
  });

  it("plays capture for en passant", () => {
    expect(
      soundForMove("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1", "e5d6"),
    ).toBe("capture");
  });

  it("plays castle for kingside castling", () => {
    expect(
      soundForMove("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1"),
    ).toBe("castle");
  });

  it("plays castle for queenside castling", () => {
    expect(
      soundForMove("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1c1"),
    ).toBe("castle");
  });

  it("plays check when the move leaves the opponent in check", () => {
    expect(soundForMove("4k3/8/8/8/8/8/8/4R2K w - - 0 1", "e1e7")).toBe(
      "check",
    );
  });

  it("plays check rather than capture when the capture is a check", () => {
    expect(soundForMove("4k3/4p3/8/8/8/8/8/4R2K w - - 0 1", "e1e7")).toBe(
      "check",
    );
  });
});

describe("soundForReplayStep", () => {
  const start =
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
  const plies = [
    {
      uci: "e2e4",
      fenAfter: "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
    },
    {
      uci: "d7d5",
      fenAfter: "rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2",
    },
    {
      uci: "e4d5",
      fenAfter: "rnbqkbnr/ppp1pppp/8/3P4/8/8/PPPP1PPP/RNBQKBNR b KQkq - 0 2",
    },
  ];

  it("plays the move just made when stepping forward", () => {
    expect(soundForReplayStep(start, plies, 0, 1)).toBe("move");
    expect(soundForReplayStep(start, plies, 2, 3)).toBe("capture");
  });

  it("plays the move just undone when stepping back one ply", () => {
    expect(soundForReplayStep(start, plies, 3, 2)).toBe("capture");
  });

  it("plays only the move landed on when jumping", () => {
    expect(soundForReplayStep(start, plies, 0, 3)).toBe("capture");
    expect(soundForReplayStep(start, plies, 3, 1)).toBe("move");
  });

  it("plays nothing when staying put or returning to the start", () => {
    expect(soundForReplayStep(start, plies, 2, 2)).toBeNull();
    expect(soundForReplayStep(start, plies, 3, 0)).toBeNull();
  });
});

describe("SOUND_SRC", () => {
  it("ships a real mp3 for every cue and no leftover unlabeled files", () => {
    const names = readdirSync(soundsDir);
    expect(names).toContain("SOUNDS.LICENSE");

    for (const cue of shippedCues) {
      const file = SOUND_SRC[cue].replace(/^\/sounds\//, "");
      expect(names).toContain(file);
      // The previous unlabeled set included 17-byte text stubs named Victory/Draw/Defeat.
      expect(statSync(join(soundsDir, file)).size).toBeGreaterThan(1000);
    }

    expect(names.filter((name) => name.endsWith(".mp3")).sort()).toEqual(
      shippedCues.map((cue) => SOUND_SRC[cue].replace(/^\/sounds\//, "")).sort(),
    );
  });
});
