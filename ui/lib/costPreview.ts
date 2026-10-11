import {
  chebyshev,
  fileIndex,
  parseBoard,
  rankIndex,
  type PieceChar,
  type Square,
} from "./fen";
import { presetToCustomConfig } from "./customConfig";
import type { EconomyConfig } from "./types";
import type { Preset } from "./presets";

export interface CostPreviewResult {
  moverNet: number;
  capture: boolean;
  victimType: PieceChar | null;
  opponentLoss: number;
  hasCheckChip: boolean;
}

function pieceValueOf(economy: EconomyConfig, piece: PieceChar): number {
  switch (piece) {
    case "p":
      return economy.pieceValues.pawn;
    case "n":
      return economy.pieceValues.knight;
    case "b":
      return economy.pieceValues.bishop;
    case "r":
      return economy.pieceValues.rook;
    case "q":
      return economy.pieceValues.queen;
    case "k":
      return 0; // king never captured
  }
}

function moveCostPerPieceOf(economy: EconomyConfig, piece: PieceChar): number {
  switch (piece) {
    case "p":
      return economy.moveCostPerPiece.pawn;
    case "n":
      return economy.moveCostPerPiece.knight;
    case "b":
      return economy.moveCostPerPiece.bishop;
    case "r":
      return economy.moveCostPerPiece.rook;
    case "q":
      return economy.moveCostPerPiece.queen;
    case "k":
      return economy.moveCostPerPiece.king;
  }
}

/**
 * The seconds a capture moves: added to the captor when `captureCaptorGainEnabled`, taken off the
 * victim when `captureVictimLossEnabled`, both off the same base. Extracted from
 * `computeCostPreview` below so the play page can re-derive a `CaptureGain` total from a move log
 * that carries no clock events (C-1-2) using the same expression the preview prices a move with,
 * rather than a second copy of it.
 */
export function captureBaseSeconds(
  economy: EconomyConfig,
  victim: PieceChar,
): number {
  return (
    pieceValueOf(economy, victim) * economy.captureMult * economy.economyScale
  );
}

export function movementToll(
  economy: EconomyConfig,
  pieceChar: PieceChar,
  dist: number,
): number {
  switch (economy.moveCostMode) {
    case "None":
      return 0;
    case "Flat":
      return economy.moveCostFlat;
    case "PerPiece":
      return moveCostPerPieceOf(economy, pieceChar);
    case "Distance":
      return economy.moveCostPerSquare * dist;
    case "PerPieceXDistance":
      return moveCostPerPieceOf(economy, pieceChar) * dist;
  }
}

function isCastlingMove(
  pieceChar: PieceChar,
  from: Square,
  to: Square,
): boolean {
  if (pieceChar !== "k") return false;
  const fromFile = fileIndex(from);
  const toFile = fileIndex(to);
  const fromRank = rankIndex(from);
  const toRank = rankIndex(to);
  // e-file (4) king moving to g-file (6) or c-file (2) on the same rank.
  return (
    fromFile === 4 && fromRank === toRank && (toFile === 6 || toFile === 2)
  );
}

/**
 * Builds the `EconomyConfig` subset for a rated preset, for callers (older code paths, or a game
 * page before the server's `economy` field has arrived) that only have a `Preset`, not a live
 * `state.economy`. Reuses `presetToCustomConfig` so it stays exact, including per-pool
 * `economyScale` (e.g. the C2 and C4 pools) which `Preset` itself doesn't carry.
 */
export function economyFromPreset(preset: Preset): EconomyConfig {
  return presetToCustomConfig(preset.key);
}

/**
 * Mirrors the server's `KiriChessEngine.Apply` clock effects the client can know from the move
 * and the economy alone: capture, movement toll, castling, promotion, and the base increment
 * `Apply` credits to the mover on every move. Excludes the check-chip amount from `moverNet`
 * (position-dependent: only whether one applies is reported via `hasCheckChip`) and repetition
 * nullification, which needs the game's position history.
 */
export function computeCostPreview(
  fen: string,
  from: Square,
  to: Square,
  economy: EconomyConfig,
  promoPiece: PieceChar = "q",
): CostPreviewResult {
  const board = parseBoard(fen);
  const mover = board[from];
  if (!mover) {
    return {
      moverNet: 0,
      capture: false,
      victimType: null,
      opponentLoss: 0,
      hasCheckChip: false,
    };
  }

  let moverNet = 0;
  let capture = false;
  let victimType: PieceChar | null = null;
  let opponentLoss = 0;

  const targetOccupant = board[to];
  const isEnPassant =
    mover.type === "p" &&
    targetOccupant === null &&
    fileIndex(from) !== fileIndex(to);

  if (targetOccupant && targetOccupant.color !== mover.color) {
    capture = true;
    victimType = targetOccupant.type;
  } else if (isEnPassant) {
    capture = true;
    victimType = "p";
  }

  if (capture && victimType) {
    const base = captureBaseSeconds(economy, victimType);
    if (economy.captureCaptorGainEnabled) {
      moverNet += base;
    }
    opponentLoss = economy.captureVictimLossEnabled ? base : 0;
  }

  const castling = isCastlingMove(mover.type, from, to);
  if (castling) {
    if (economy.moveCostMode !== "None") {
      moverNet -= economy.castleCostFlat * economy.economyScale;
    }
  } else {
    const dist = chebyshev(from, to);
    const toll = movementToll(economy, mover.type, dist) * economy.economyScale;
    moverNet -= toll;
  }

  const isPromotion =
    mover.type === "p" && (rankIndex(to) === 7 || rankIndex(to) === 0);
  if (isPromotion) {
    moverNet -=
      economy.promotionCostMult *
      pieceValueOf(economy, promoPiece) *
      economy.economyScale;
  }

  // Whole seconds, not scaled by economyScale: Apply credits BaseIncrement as-is.
  moverNet += economy.baseIncrement;

  const hasCheckChip =
    economy.checkCheckerGain > 0 || economy.checkVictimLoss > 0;

  return { moverNet, capture, victimType, opponentLoss, hasCheckChip };
}
