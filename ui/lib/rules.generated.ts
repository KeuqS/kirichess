// GENERATED FILE, DO NOT EDIT.
//
// Source of truth: config/kirichess.rules.json
// Regenerate:      npm run gen:rules   (npm run gen:rules:check verifies it in CI)
//
// The same JSON is embedded into KiriChess.Engine, so every number below is byte-for-byte the
// economy the server actually runs. Change the config, not this file.

export type PoolKey = "bounty1" | "burn1" | "steal2" | "steal1" | "burn2" | "steal3";

export type MoveCostMode =
  | "None"
  | "Flat"
  | "PerPiece"
  | "Distance"
  | "PerPieceXDistance";

export type Economy = "steal" | "burn" | "bounty";

export interface PieceInts5 {
  pawn: number;
  knight: number;
  bishop: number;
  rook: number;
  queen: number;
}

export interface PieceInts6 extends PieceInts5 {
  king: number;
}

/** A complete set of rule variables, mirrors `KiriChessConfig` field for field. */
export interface RuleValues {
  startTimeSeconds: number;
  baseIncrement: number;
  economyScale: number;
  pieceValues: PieceInts5;
  captureMult: number;
  captureCaptorGainEnabled: boolean;
  captureVictimLossEnabled: boolean;
  moveCostMode: MoveCostMode;
  moveCostPerPiece: PieceInts6;
  castleCostFlat: number;
  moveCostFlat: number;
  moveCostPerSquare: number;
  checkCheckerGain: number;
  checkVictimLoss: number;
  doubleCheckChipMultiplier: number;
  promotionCostMult: number;
  repeatedPositionNullifiesTime: boolean;
  threefoldRepetitionIsDraw: boolean;
  flagFallLoses: boolean;
  flagVsInsufficientMaterialIsDraw: boolean;
}

export interface PoolDuration {
  ceilingMinutes: number;
  beginner: string;
  intermediate: string;
  advanced: string;
  feelsLike: string;
}

export interface PoolDefinition {
  key: PoolKey;
  label: string;
  rated: boolean;
  tagline: string;
  /** The pool's fully resolved rule set (defaults with its overrides applied). */
  config: RuleValues;
  duration: PoolDuration;
}

export const RULES_SCHEMA_VERSION = 2;
export const EDITION = "Second Edition";

/** Engine defaults, and the starting point for the custom-game builder. */
export const DEFAULT_RULES: RuleValues = {
  "startTimeSeconds": 180,
  "baseIncrement": 0,
  "economyScale": 1,
  "pieceValues": {
    "pawn": 1,
    "knight": 2,
    "bishop": 2,
    "rook": 3,
    "queen": 5
  },
  "captureMult": 5,
  "captureCaptorGainEnabled": true,
  "captureVictimLossEnabled": true,
  "moveCostMode": "PerPiece",
  "moveCostPerPiece": {
    "pawn": 1,
    "knight": 2,
    "bishop": 2,
    "rook": 3,
    "queen": 4,
    "king": 1
  },
  "castleCostFlat": 3,
  "moveCostFlat": 1,
  "moveCostPerSquare": 1,
  "checkCheckerGain": 2,
  "checkVictimLoss": 2,
  "doubleCheckChipMultiplier": 2,
  "promotionCostMult": 0,
  "repeatedPositionNullifiesTime": true,
  "threefoldRepetitionIsDraw": true,
  "flagFallLoses": true,
  "flagVsInsufficientMaterialIsDraw": true
};

/** Pool keys in the order the config declares them, the order the UI presents them in. */
export const POOL_KEYS: PoolKey[] = [
  "bounty1",
  "burn1",
  "steal2",
  "steal1",
  "burn2",
  "steal3"
];

/** Keys of the pools that carry a rating. Any other config is unrated. */
export const RATED_POOL_KEYS: PoolKey[] = [
  "bounty1",
  "burn1",
  "steal2",
  "steal1",
  "burn2",
  "steal3"
];

export const POOLS: Record<PoolKey, PoolDefinition> = {
  "bounty1": {
    "key": "bounty1",
    "label": "Winder",
    "rated": true,
    "tagline": "The on-ramp.",
    "duration": {
      "ceilingMinutes": 1.8,
      "beginner": "1–1.5 min",
      "intermediate": "1–2 min",
      "advanced": "1–2.5 min",
      "feelsLike": "1m bullet"
    },
    "config": {
      "startTimeSeconds": 60,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 5,
      "captureCaptorGainEnabled": true,
      "captureVictimLossEnabled": false,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  },
  "burn1": {
    "key": "burn1",
    "label": "Ratchet",
    "rated": true,
    "tagline": "No take-backs.",
    "duration": {
      "ceilingMinutes": 2.8,
      "beginner": "1–2 min",
      "intermediate": "1.5–2.5 min",
      "advanced": "2–2.8 min",
      "feelsLike": "2m bullet"
    },
    "config": {
      "startTimeSeconds": 240,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 5,
      "captureCaptorGainEnabled": false,
      "captureVictimLossEnabled": true,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  },
  "steal2": {
    "key": "steal2",
    "label": "Fulcrum",
    "rated": true,
    "tagline": "Double stakes.",
    "duration": {
      "ceilingMinutes": 7.3,
      "beginner": "2–3 min",
      "intermediate": "3–5 min",
      "advanced": "4–7 min",
      "feelsLike": "3m blitz"
    },
    "config": {
      "startTimeSeconds": 300,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 10,
      "captureCaptorGainEnabled": true,
      "captureVictimLossEnabled": true,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  },
  "steal1": {
    "key": "steal1",
    "label": "Anchor",
    "rated": true,
    "tagline": "The rated default.",
    "duration": {
      "ceilingMinutes": 9.3,
      "beginner": "4–7 min",
      "intermediate": "6–9 min",
      "advanced": "8–10 min",
      "feelsLike": "5m blitz"
    },
    "config": {
      "startTimeSeconds": 360,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 5,
      "captureCaptorGainEnabled": true,
      "captureVictimLossEnabled": true,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  },
  "burn2": {
    "key": "burn2",
    "label": "Mainspring",
    "rated": true,
    "tagline": "Every trade bleeds.",
    "duration": {
      "ceilingMinutes": 22,
      "beginner": "9–13 min",
      "intermediate": "13–17 min",
      "advanced": "16–21 min",
      "feelsLike": "10m rapid"
    },
    "config": {
      "startTimeSeconds": 900,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 10,
      "captureCaptorGainEnabled": false,
      "captureVictimLossEnabled": true,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  },
  "steal3": {
    "key": "steal3",
    "label": "Eclipse",
    "rated": true,
    "tagline": "Room to think.",
    "duration": {
      "ceilingMinutes": 27,
      "beginner": "10–15 min",
      "intermediate": "14–20 min",
      "advanced": "18–26 min",
      "feelsLike": "15m rapid"
    },
    "config": {
      "startTimeSeconds": 900,
      "baseIncrement": 0,
      "economyScale": 1,
      "pieceValues": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 5
      },
      "captureMult": 20,
      "captureCaptorGainEnabled": true,
      "captureVictimLossEnabled": true,
      "moveCostMode": "PerPiece",
      "moveCostPerPiece": {
        "pawn": 1,
        "knight": 2,
        "bishop": 2,
        "rook": 3,
        "queen": 4,
        "king": 1
      },
      "castleCostFlat": 3,
      "moveCostFlat": 1,
      "moveCostPerSquare": 1,
      "checkCheckerGain": 2,
      "checkVictimLoss": 2,
      "doubleCheckChipMultiplier": 2,
      "promotionCostMult": 0,
      "repeatedPositionNullifiesTime": true,
      "threefoldRepetitionIsDraw": true,
      "flagFallLoses": true,
      "flagVsInsufficientMaterialIsDraw": true
    }
  }
};

// -------------------------------------------------------------------------------------------
// Derived tables, computed from the rule variables above by scripts/gen-rules.mjs, so they
// recompute themselves whenever a rule changes instead of going quietly stale.
// -------------------------------------------------------------------------------------------

/** True when every rated pool prices moves identically (what makes the toll table universal). */
export const TOLLS_ARE_UNIVERSAL = true;

/** True when every rated pool prices checks identically (what makes the check table universal). */
export const CHIPS_ARE_UNIVERSAL = true;

export const CAPTURE_TABLE = [
  {
    "piece": "pawn",
    "label": "Pawn",
    "glyph": "♟",
    "points": 1,
    "amount": 5,
    "steal": "±5s",
    "bounty": "+5s",
    "burn": "−5s"
  },
  {
    "piece": "knight",
    "label": "Knight",
    "glyph": "♞",
    "points": 2,
    "amount": 10,
    "steal": "±10s",
    "bounty": "+10s",
    "burn": "−10s"
  },
  {
    "piece": "bishop",
    "label": "Bishop",
    "glyph": "♝",
    "points": 2,
    "amount": 10,
    "steal": "±10s",
    "bounty": "+10s",
    "burn": "−10s"
  },
  {
    "piece": "rook",
    "label": "Rook",
    "glyph": "♜",
    "points": 3,
    "amount": 15,
    "steal": "±15s",
    "bounty": "+15s",
    "burn": "−15s"
  },
  {
    "piece": "queen",
    "label": "Queen",
    "glyph": "♛",
    "points": 5,
    "amount": 25,
    "steal": "±25s",
    "bounty": "+25s",
    "burn": "−25s"
  }
] as const;

export const TOLL_TABLE = [
  {
    "piece": "pawn",
    "label": "Pawn",
    "glyph": "♟",
    "toll": 1
  },
  {
    "piece": "knight",
    "label": "Knight",
    "glyph": "♞",
    "toll": 2
  },
  {
    "piece": "bishop",
    "label": "Bishop",
    "glyph": "♝",
    "toll": 2
  },
  {
    "piece": "rook",
    "label": "Rook",
    "glyph": "♜",
    "toll": 3
  },
  {
    "piece": "queen",
    "label": "Queen",
    "glyph": "♛",
    "toll": 4
  },
  {
    "piece": "king",
    "label": "King",
    "glyph": "♚",
    "toll": 1
  },
  {
    "piece": "castle",
    "label": "Castling (either side)",
    "glyph": "⟲",
    "toll": 3
  }
] as const;

export const CHECK_PROFIT_TABLE = [
  {
    "piece": "Queen",
    "breakdown": "−4 (toll) +2 (chip) = −2",
    "yourClock": -2,
    "theirClock": -2,
    "gap": 0,
    "verdict": "±0"
  },
  {
    "piece": "Rook",
    "breakdown": "−3 (toll) +2 (chip) = −1",
    "yourClock": -1,
    "theirClock": -2,
    "gap": 1,
    "verdict": "+1"
  },
  {
    "piece": "Knight / Bishop",
    "breakdown": "−2 (toll) +2 (chip) = 0",
    "yourClock": 0,
    "theirClock": -2,
    "gap": 2,
    "verdict": "+2"
  },
  {
    "piece": "Pawn",
    "breakdown": "−1 (toll) +2 (chip) = +1",
    "yourClock": 1,
    "theirClock": -2,
    "gap": 3,
    "verdict": "+3"
  }
] as const;

export const TRADE_MATH_TABLE = [
  {
    "economy": "steal",
    "label": "Steal (5, 5)",
    "you": "−10 +10 = 0",
    "them": "0",
    "effect": "unchanged"
  },
  {
    "economy": "bounty",
    "label": "Bounty (5, 0)",
    "you": "+10",
    "them": "+10",
    "effect": "both profit"
  },
  {
    "economy": "burn",
    "label": "Burn (0, 5)",
    "you": "−10",
    "them": "−10",
    "effect": "both drain"
  }
] as const;

export const POOL_TABLE = [
  {
    "key": "bounty1",
    "label": "Winder",
    "tagline": "The on-ramp.",
    "rated": true,
    "time": "1+0",
    "startTimeSeconds": 60,
    "multiplier": "C1",
    "economy": "bounty",
    "tag": "1+0 - Bounty - C1"
  },
  {
    "key": "burn1",
    "label": "Ratchet",
    "tagline": "No take-backs.",
    "rated": true,
    "time": "4+0",
    "startTimeSeconds": 240,
    "multiplier": "C1",
    "economy": "burn",
    "tag": "4+0 - Burn - C1"
  },
  {
    "key": "steal2",
    "label": "Fulcrum",
    "tagline": "Double stakes.",
    "rated": true,
    "time": "5+0",
    "startTimeSeconds": 300,
    "multiplier": "C2",
    "economy": "steal",
    "tag": "5+0 - Steal - C2"
  },
  {
    "key": "steal1",
    "label": "Anchor",
    "tagline": "The rated default.",
    "rated": true,
    "time": "6+0",
    "startTimeSeconds": 360,
    "multiplier": "C1",
    "economy": "steal",
    "tag": "6+0 - Steal - C1"
  },
  {
    "key": "burn2",
    "label": "Mainspring",
    "tagline": "Every trade bleeds.",
    "rated": true,
    "time": "15+0",
    "startTimeSeconds": 900,
    "multiplier": "C2",
    "economy": "burn",
    "tag": "15+0 - Burn - C2"
  },
  {
    "key": "steal3",
    "label": "Eclipse",
    "tagline": "Room to think.",
    "rated": true,
    "time": "15+0",
    "startTimeSeconds": 900,
    "multiplier": "C4",
    "economy": "steal",
    "tag": "15+0 - Steal - C4"
  }
] as const;

/** Per-pool prices, for the sentences that quote one inline. */
export const POOL_ECONOMY_TABLE = [
  {
    "key": "bounty1",
    "label": "Winder",
    "minutes": 1,
    "perPoint": 5,
    "multiplierRatio": 1,
    "amount": {
      "pawn": 5,
      "knight": 10,
      "bishop": 10,
      "rook": 15,
      "queen": 25
    },
    "swing": {
      "pawn": 5,
      "knight": 10,
      "bishop": 10,
      "rook": 15,
      "queen": 25
    },
    "tollSharePct": 135,
    "queenSwingSharePct": 42,
    "checksToRecoverQueen": 8
  },
  {
    "key": "burn1",
    "label": "Ratchet",
    "minutes": 4,
    "perPoint": 5,
    "multiplierRatio": 1,
    "amount": {
      "pawn": 5,
      "knight": 10,
      "bishop": 10,
      "rook": 15,
      "queen": 25
    },
    "swing": {
      "pawn": 5,
      "knight": 10,
      "bishop": 10,
      "rook": 15,
      "queen": 25
    },
    "tollSharePct": 34,
    "queenSwingSharePct": 10,
    "checksToRecoverQueen": 8
  },
  {
    "key": "steal2",
    "label": "Fulcrum",
    "minutes": 5,
    "perPoint": 10,
    "multiplierRatio": 2,
    "amount": {
      "pawn": 10,
      "knight": 20,
      "bishop": 20,
      "rook": 30,
      "queen": 50
    },
    "swing": {
      "pawn": 20,
      "knight": 40,
      "bishop": 40,
      "rook": 60,
      "queen": 100
    },
    "tollSharePct": 27,
    "queenSwingSharePct": 33,
    "checksToRecoverQueen": 33
  },
  {
    "key": "steal1",
    "label": "Anchor",
    "minutes": 6,
    "perPoint": 5,
    "multiplierRatio": 1,
    "amount": {
      "pawn": 5,
      "knight": 10,
      "bishop": 10,
      "rook": 15,
      "queen": 25
    },
    "swing": {
      "pawn": 10,
      "knight": 20,
      "bishop": 20,
      "rook": 30,
      "queen": 50
    },
    "tollSharePct": 23,
    "queenSwingSharePct": 14,
    "checksToRecoverQueen": 17
  },
  {
    "key": "burn2",
    "label": "Mainspring",
    "minutes": 15,
    "perPoint": 10,
    "multiplierRatio": 2,
    "amount": {
      "pawn": 10,
      "knight": 20,
      "bishop": 20,
      "rook": 30,
      "queen": 50
    },
    "swing": {
      "pawn": 10,
      "knight": 20,
      "bishop": 20,
      "rook": 30,
      "queen": 50
    },
    "tollSharePct": 9,
    "queenSwingSharePct": 6,
    "checksToRecoverQueen": 17
  },
  {
    "key": "steal3",
    "label": "Eclipse",
    "minutes": 15,
    "perPoint": 20,
    "multiplierRatio": 4,
    "amount": {
      "pawn": 20,
      "knight": 40,
      "bishop": 40,
      "rook": 60,
      "queen": 100
    },
    "swing": {
      "pawn": 40,
      "knight": 80,
      "bishop": 80,
      "rook": 120,
      "queen": 200
    },
    "tollSharePct": 9,
    "queenSwingSharePct": 22,
    "checksToRecoverQueen": 67
  }
] as const;

export const THINKING_BUDGET_TABLE = [
  {
    "key": "bounty1",
    "label": "Winder",
    "poolSeconds": 60,
    "tollDrain": 81,
    "economyDrain": 75,
    "drain": "~81s tolls − ~75s bounty",
    "perMove": 1.4,
    "feelsLike": "1m bullet"
  },
  {
    "key": "burn1",
    "label": "Ratchet",
    "poolSeconds": 240,
    "tollDrain": 81,
    "economyDrain": -75,
    "drain": "~81s tolls + ~75s burn",
    "perMove": 2.1,
    "feelsLike": "2m bullet"
  },
  {
    "key": "steal2",
    "label": "Fulcrum",
    "poolSeconds": 300,
    "tollDrain": 81,
    "economyDrain": 0,
    "drain": "~81s tolls",
    "perMove": 5.5,
    "feelsLike": "3m blitz"
  },
  {
    "key": "steal1",
    "label": "Anchor",
    "poolSeconds": 360,
    "tollDrain": 81,
    "economyDrain": 0,
    "drain": "~81s tolls",
    "perMove": 7,
    "feelsLike": "5m blitz"
  },
  {
    "key": "burn2",
    "label": "Mainspring",
    "poolSeconds": 900,
    "tollDrain": 81,
    "economyDrain": -150,
    "drain": "~81s tolls + ~150s burn",
    "perMove": 16.7,
    "feelsLike": "10m rapid"
  },
  {
    "key": "steal3",
    "label": "Eclipse",
    "poolSeconds": 900,
    "tollDrain": 81,
    "economyDrain": 0,
    "drain": "~81s tolls",
    "perMove": 20.5,
    "feelsLike": "15m rapid"
  }
] as const;

export const DURATION_TABLE = [
  {
    "key": "bounty1",
    "label": "Winder",
    "nominalMinutes": 1,
    "ceilingMinutes": 1.8,
    "beginner": "1–1.5 min",
    "intermediate": "1–2 min",
    "advanced": "1–2.5 min"
  },
  {
    "key": "burn1",
    "label": "Ratchet",
    "nominalMinutes": 4,
    "ceilingMinutes": 2.8,
    "beginner": "1–2 min",
    "intermediate": "1.5–2.5 min",
    "advanced": "2–2.8 min"
  },
  {
    "key": "steal2",
    "label": "Fulcrum",
    "nominalMinutes": 5,
    "ceilingMinutes": 7.3,
    "beginner": "2–3 min",
    "intermediate": "3–5 min",
    "advanced": "4–7 min"
  },
  {
    "key": "steal1",
    "label": "Anchor",
    "nominalMinutes": 6,
    "ceilingMinutes": 9.3,
    "beginner": "4–7 min",
    "intermediate": "6–9 min",
    "advanced": "8–10 min"
  },
  {
    "key": "burn2",
    "label": "Mainspring",
    "nominalMinutes": 15,
    "ceilingMinutes": 22,
    "beginner": "9–13 min",
    "intermediate": "13–17 min",
    "advanced": "16–21 min"
  },
  {
    "key": "steal3",
    "label": "Eclipse",
    "nominalMinutes": 15,
    "ceilingMinutes": 27,
    "beginner": "10–15 min",
    "intermediate": "14–20 min",
    "advanced": "18–26 min"
  }
] as const;

export const ENDGAME_TABLE = [
  {
    "name": "K+Q+R vs K",
    "work": "Ladder, heavy pieces",
    "moves": 6,
    "bill": 21,
    "mix": "3× queen, 3× rook"
  },
  {
    "name": "K+2R vs K",
    "work": "Pure rook ladder, king never moves",
    "moves": 8,
    "bill": 24,
    "mix": "8× rook"
  },
  {
    "name": "K+Q vs K",
    "work": "Queen boxes, king walks",
    "moves": 15,
    "bill": 48,
    "mix": "11× queen, 4× king"
  },
  {
    "name": "K+2B vs K",
    "work": "Bishops herd, king supports",
    "moves": 28,
    "bill": 48,
    "mix": "20× bishop, 8× king"
  },
  {
    "name": "K+P vs K",
    "work": "Escort the pawn, then the queen bill",
    "moves": 25,
    "bill": 58,
    "mix": "6× pawn, 8× king, 11× queen"
  },
  {
    "name": "K+R vs K",
    "work": "Rook boxes, king shoulders",
    "moves": 28,
    "bill": 70,
    "mix": "21× rook, 7× king"
  },
  {
    "name": "K+B+N vs K",
    "work": "The hardest mate in chess",
    "moves": 50,
    "bill": 88,
    "mix": "20× bishop, 18× knight, 12× king"
  },
  {
    "name": "K+Q vs K+R",
    "work": "Queen-heavy grind",
    "moves": 30,
    "bill": 105,
    "mix": "25× queen, 5× king"
  }
] as const;

/** Prices for the events that are neither an ordinary move, a capture, nor a check. */
export const PRICED_EVENTS = {
  "promotionIsFree": true,
  "promotionQueenCost": 0,
  "pawnToll": 1,
  "pawnCapture": 5,
  "castleCost": 3,
  "kingToll": 1,
  "rookToll": 3,
  "doubleCheckGain": 4,
  "doubleCheckLoss": 4,
  "checkGain": 2,
  "checkLoss": 2
} as const;

/** The flags that govern how games end. */
export const ENDING_RULES = {
  "flagFallLoses": true,
  "flagVsInsufficientMaterialIsDraw": true,
  "threefoldRepetitionIsDraw": true,
  "repeatedPositionNullifiesTime": true
} as const;

/** Modelling assumptions behind the explanatory tables (not engine rules). */
export const DOC_MODEL = {
  "movesPerPlayer": 40,
  "moveFrequency": {
    "pawn": 0.3,
    "knight": 0.14,
    "bishop": 0.14,
    "rook": 0.19,
    "queen": 0.12,
    "king": 0.11
  },
  "materialTradedPoints": {
    "value": 15
  },
  "endgames": [
    {
      "name": "K+Q+R vs K",
      "work": "Ladder, heavy pieces",
      "mix": {
        "queen": 3,
        "rook": 3
      }
    },
    {
      "name": "K+2R vs K",
      "work": "Pure rook ladder, king never moves",
      "mix": {
        "rook": 8
      }
    },
    {
      "name": "K+Q vs K",
      "work": "Queen boxes, king walks",
      "mix": {
        "queen": 11,
        "king": 4
      }
    },
    {
      "name": "K+2B vs K",
      "work": "Bishops herd, king supports",
      "mix": {
        "bishop": 20,
        "king": 8
      }
    },
    {
      "name": "K+P vs K",
      "work": "Escort the pawn, then the queen bill",
      "mix": {
        "pawn": 6,
        "king": 8,
        "queen": 11
      }
    },
    {
      "name": "K+R vs K",
      "work": "Rook boxes, king shoulders",
      "mix": {
        "rook": 21,
        "king": 7
      }
    },
    {
      "name": "K+B+N vs K",
      "work": "The hardest mate in chess",
      "mix": {
        "bishop": 20,
        "knight": 18,
        "king": 12
      }
    },
    {
      "name": "K+Q vs K+R",
      "work": "Queen-heavy grind",
      "mix": {
        "queen": 25,
        "king": 5
      }
    }
  ]
} as const;
