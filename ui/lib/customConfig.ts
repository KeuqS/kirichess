// The Custom game builder's config shape. Every field maps 1:1 (camelCase) onto `KiriChessConfig`
// (src/KiriChess.Engine/Config/KiriChessConfig.cs) and the server's `CustomConfigDto`
// (src/KiriChess.Server/Games/GameDtos.cs).
//
// The values come from `rules.generated.ts`, generated from config/kirichess.rules.json, the same
// document the engine embeds. There is nothing to keep in sync by hand.

import {
  DEFAULT_RULES,
  POOLS,
  type MoveCostMode,
  type PieceInts5,
  type PieceInts6,
  type RuleValues,
} from "./rules.generated";
import type { PresetKey } from "./presets";

export type { PieceInts5, PieceInts6 };

/** A full, tweakable rule set. Structurally identical to the generated `RuleValues`. */
export type CustomGameConfig = RuleValues;

/**
 * Every value `moveCostMode` can hold, in the order the builder offers them. `MoveCostMode` is a
 * generated union type and a type cannot be iterated at runtime, so this is the one hand-written
 * list of its members: the builder renders from it, rather than keeping a second copy that
 * can drift.
 */
export const MOVE_COST_MODES: readonly MoveCostMode[] = [
  "None",
  "Flat",
  "PerPiece",
  "Distance",
  "PerPieceXDistance",
];

/** Deep copy: the generated tables are module-level constants and must never be mutated. */
export function cloneConfig(config: RuleValues): CustomGameConfig {
  return {
    ...config,
    pieceValues: { ...config.pieceValues },
    moveCostPerPiece: { ...config.moveCostPerPiece },
  };
}

/** Matches the engine's `new KiriChessConfig()` defaults exactly. */
export const DEFAULT_CUSTOM_CONFIG: CustomGameConfig =
  cloneConfig(DEFAULT_RULES);

/** Loads a rated pool as a full, tweakable config ("load a preset then tweak"). */
export function presetToCustomConfig(key: PresetKey): CustomGameConfig {
  return cloneConfig(POOLS[key].config);
}
