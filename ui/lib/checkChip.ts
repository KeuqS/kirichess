import { Chess, type Color, type Square } from "chess.js";

/**
 * The check-chip half of `KiriChessEngine.Apply` (Rule 3). `computeCostPreview` deliberately
 * leaves this out: the amount depends on the position after the move, not on the move alone.
 *
 * `chess` is that position. A checkmate pays nothing. A double check - two of the mover's
 * pieces attack the king, pins included, the same attack count the engine's `IsDoubleCheck`
 * uses - multiplies both halves by `doubleCheckChipMultiplier`. Multiplier 1, and a chip that
 * is already zero on both sides, are left alone: neither can change an amount.
 */
export interface CheckChipConfig {
  checkCheckerGain: number;
  checkVictimLoss: number;
  economyScale: number;
  doubleCheckChipMultiplier: number;
}

export interface CheckChipDeltas {
  mover: number;
  opponent: number;
}

const NONE: CheckChipDeltas = { mover: 0, opponent: 0 };

export function checkChipDeltas(
  chess: Chess,
  config: CheckChipConfig,
): CheckChipDeltas {
  if (!chess.isCheck() || chess.isCheckmate()) return NONE;

  let gain = config.checkCheckerGain * config.economyScale;
  let loss = config.checkVictimLoss * config.economyScale;
  // Both halves already zero stay zero. Skip the attacker scan, and avoid -0 from negating a
  // zero loss, which would fail a numeric equality check against the engine's integer 0.
  if (gain === 0 && loss === 0) return NONE;
  if (config.doubleCheckChipMultiplier !== 1 && isDoubleCheck(chess)) {
    gain *= config.doubleCheckChipMultiplier;
    loss *= config.doubleCheckChipMultiplier;
  }

  return { mover: gain, opponent: loss === 0 ? 0 : -loss };
}

function isDoubleCheck(chess: Chess): boolean {
  const victim = chess.turn();
  const king = kingSquare(chess, victim);
  if (!king) return false;
  const checker: Color = victim === "w" ? "b" : "w";
  return chess.attackers(king, checker).length >= 2;
}

function kingSquare(chess: Chess, color: Color): Square | null {
  for (const row of chess.board()) {
    for (const piece of row) {
      if (piece?.type === "k" && piece.color === color) return piece.square;
    }
  }
  return null;
}
