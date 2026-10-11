// Pool metadata for the UI. Every value here comes from `rules.generated.ts`, which is generated
// from config/kirichess.rules.json, the same document the engine embeds. Nothing in this file
// hardcodes a rule number; it only reshapes the generated data into the view model the components
// already expect.

import {
  POOLS,
  POOL_KEYS,
  type Economy,
  type MoveCostMode,
  type PoolKey,
} from "./rules.generated";

export type { MoveCostMode, Economy };

/** Historical alias: pools have always been called "presets" in the UI layer. */
export type PresetKey = PoolKey;

export interface Preset {
  key: PresetKey;
  label: string;
  tagline: string;
  moveCostMode: MoveCostMode;
  captorGain: boolean;
  victimLoss: boolean;
  startTimeSeconds: number;
  /** e.g. "6+0": the time control as players read it. */
  time: string;
  /** e.g. "C2" for capture-only scaling, "F2" when the whole economy scales. */
  multiplier: string;
  /** e.g. "6+0 - Steal - C1": the full one-line descriptor. */
  tag: string;
  economy: Economy;
}

function toPreset(key: PresetKey): Preset {
  const pool = POOLS[key];
  const c = pool.config;
  const economy: Economy =
    c.captureCaptorGainEnabled && c.captureVictimLossEnabled
      ? "steal"
      : c.captureCaptorGainEnabled
        ? "bounty"
        : "burn";
  const time = timeControlLabel(c.startTimeSeconds, c.baseIncrement);
  const multiplier = multiplierLabel(c.captureMult, c.economyScale);

  return {
    key,
    label: pool.label,
    tagline: pool.tagline,
    moveCostMode: c.moveCostMode,
    captorGain: c.captureCaptorGainEnabled,
    victimLoss: c.captureVictimLossEnabled,
    startTimeSeconds: c.startTimeSeconds,
    time,
    multiplier,
    tag: `${time} - ${economy[0].toUpperCase()}${economy.slice(1)} - ${multiplier}`,
    economy,
  };
}

export const PRESETS: Record<PresetKey, Preset> = Object.fromEntries(
  POOL_KEYS.map((key) => [key, toPreset(key)]),
) as Record<PresetKey, Preset>;

/** Declaration order from the config: the order the UI presents the pools in. */
export const PRESET_ORDER: PresetKey[] = [...POOL_KEYS];

export function isPresetKey(value: string): value is PresetKey {
  return value in PRESETS;
}

export function economyOf(
  preset: Pick<Preset, "captorGain" | "victimLoss">,
): Economy {
  return captureEconomy(preset.captorGain, preset.victimLoss) ?? "burn";
}

/**
 * Steal, burn, or bounty. Null when captures move no clock at all.
 * `economyOf` folds that case into burn for the builder's badge, which always has to paint one of
 * the three; a consent summary must not call "no capture time" a burn economy.
 */
export function captureEconomy(
  captorGain: boolean,
  victimLoss: boolean,
): Economy | null {
  if (captorGain && victimLoss) return "steal";
  if (captorGain) return "bounty";
  if (victimLoss) return "burn";
  return null;
}

/** "3+0" when the clock is a whole number of minutes, "123s+4" when it is not. */
export function timeControlLabel(
  startTimeSeconds: number,
  baseIncrement: number,
): string {
  const clock =
    startTimeSeconds % 60 === 0
      ? String(startTimeSeconds / 60)
      : `${startTimeSeconds}s`;
  return `${clock}+${baseIncrement}`;
}

/**
 * The pool-tile multiplier: C scales captures only, F scales the whole economy. The capture
 * ratio is against steal1, the same base `toPreset` has always used.
 */
export function multiplierLabel(
  captureMult: number,
  economyScale: number,
): string {
  const capture = captureMult / POOLS.steal1.config.captureMult;
  if (economyScale === 1) return `C${capture}`;
  if (capture === 1) return `F${economyScale}`;
  return `C${capture}-F${economyScale}`;
}

/** CSS custom property name to accent an economy with (e.g. a Card border). */
export const ECONOMY_ACCENT_VAR: Record<Economy, string> = {
  steal: "--economy-steal",
  burn: "--economy-burn",
  bounty: "--economy-bounty",
};

const ECONOMY_KEYS: Economy[] = ["steal", "burn", "bounty"];

/**
 * Generated display names for the three capture economies (Steal / Burn / Bounty), derived from
 * the config keys the same way `PRESETS[key].tag` already capitalises them. Hand these to `t()`
 * so home-page gloss copy never writes the words into the catalogue (P-2-5).
 */
export const ECONOMY_NAMES: Record<Economy, string> = Object.fromEntries(
  ECONOMY_KEYS.map((key) => [key, `${key[0].toUpperCase()}${key.slice(1)}`]),
) as Record<Economy, string>;

/**
 * Every pool's display name, keyed by its frozen config key: the value object to hand any
 * translated string that names a pool.
 *
 * Prose in `messages/en.json` writes `{steal1}`, never the word, because the name is a display
 * string `config/kirichess.rules.json` owns and the key is a permanent storage id (it is what sits
 * in `Games.RatingPool`). Spreading this into a `t()` call keeps a rename a one-file edit:
 *
 *   t("steal2Para1", POOL_NAMES)
 *
 * `PoolLabelDriftTests` fails the build if a pool label is written out by hand in `ui/` again.
 */
export const POOL_NAMES: Record<PresetKey, string> = Object.fromEntries(
  POOL_KEYS.map((key) => [key, POOLS[key].label]),
) as Record<PresetKey, string>;
