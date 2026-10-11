/**
 * Simulated bot thinking time (Phase 7.2).
 *
 * Why this exists at all: the server charges the mover whole seconds of think time, measured from
 * when it processed the previous move (`GameSession.ElapsedSeconds`). Stockfish replies in a few
 * hundred milliseconds, which floored to **zero seconds charged, every move**, so before this
 * module the bot's clock only ever moved via tolls and capture economy, and a human literally could
 * not flag it. In a variant whose win condition is the clock, that made bot games a different game.
 *
 * The server side of that hole is closed as of C-4-2: it still bills whole seconds, but the
 * sub-second remainder is carried into the same side's next turn instead of being discarded, so
 * three 400ms replies now cost the bot a second rather than nothing. This module is still what
 * makes a bot spend its clock at a human scale, and the numbers below are unchanged; what is gone
 * is the free ride under a second, which is why the note further down has been corrected too.
 *
 * So think time here is not cosmetic pacing. It is a **resource the bot spends**, and how well it
 * budgets that resource is the difficulty axis: Stockfish picks the move, this picks the price. A
 * clock-blind engine can still feel like a KiriChess opponent if it manages its clock like one,
 * and mismanaging it is exactly what a weak player does.
 *
 * The C# game-mode simulator (`simulator/KiriChess.Simulator/BotClock.cs`) is a line-for-line port
 * of the numbers and clamp order below. Change them in both places or pool stats will describe a
 * different opponent than the one on the site.
 */

/** How a tier spends its clock. Separate from Stockfish's skill/depth, which is move quality only. */
export interface ClockPolicy {
  /**
   * Fraction of its *remaining* clock the bot will spend on a normal move. Weak tiers overspend and
   * will genuinely flag themselves in a long game; strong tiers keep something in reserve.
   */
  budgetFraction: number;
  /** Never think longer than this, however much clock is left: nobody waits 30s for a bot. */
  maxThinkMs: number;
  /** Never reply faster than this; an instant reply reads as a computer, not an opponent. */
  minThinkMs: number;
  /**
   * Seconds the bot refuses to dip below when budgeting. Weak tiers reserve almost nothing, which
   * is what makes them lose on time; strong tiers protect their endgame.
   */
  reserveSeconds: number;
}

export interface BotTier {
  key: string;
  label: string;
  /** One player-facing sentence, describing how it *plays the clock*: the thing that varies here. */
  blurb: string;
  /** Lower bound of the Elo band this tier covers. */
  minElo: number;
  clock: ClockPolicy;
}

/**
 * The ladder. Move quality still comes from the existing Elo→skill/depth mapping in `bot.ts`; what
 * changes across tiers here is clock discipline. The progression is deliberate: a novice burns 12%
 * of its clock on every move and keeps no reserve (it will flag itself, and a beginner should be
 * beatable on time), while a master spends 3% and protects 15 seconds.
 */
export const BOT_TIERS: BotTier[] = [
  {
    key: "novice",
    label: "Novice",
    blurb: "Overthinks everything and runs itself out of time.",
    minElo: 0,
    clock: {
      budgetFraction: 0.12,
      maxThinkMs: 6000,
      minThinkMs: 600,
      reserveSeconds: 0,
    },
  },
  {
    key: "casual",
    label: "Casual",
    blurb: "Thinks too long in complicated positions.",
    minElo: 900,
    clock: {
      budgetFraction: 0.08,
      maxThinkMs: 5000,
      minThinkMs: 500,
      reserveSeconds: 2,
    },
  },
  {
    key: "club",
    label: "Club",
    blurb: "Reasonable clock sense, still spends on sharp positions.",
    minElo: 1300,
    clock: {
      budgetFraction: 0.055,
      maxThinkMs: 4000,
      minThinkMs: 400,
      reserveSeconds: 5,
    },
  },
  {
    key: "expert",
    label: "Expert",
    blurb: "Budgets well and speeds up when the clock gets short.",
    minElo: 1700,
    clock: {
      budgetFraction: 0.04,
      maxThinkMs: 3000,
      minThinkMs: 350,
      reserveSeconds: 10,
    },
  },
  {
    key: "master",
    label: "Master",
    blurb: "Disciplined: protects its clock and rarely gets flagged.",
    minElo: 2100,
    clock: {
      budgetFraction: 0.03,
      maxThinkMs: 2500,
      minThinkMs: 300,
      reserveSeconds: 15,
    },
  },
];

/** The tier covering a given Elo. Bands are contiguous, so this never returns undefined. */
export function tierForElo(elo: number): BotTier {
  let match = BOT_TIERS[0];
  for (const tier of BOT_TIERS) {
    if (elo >= tier.minElo) match = tier;
  }
  return match;
}

export interface ThinkTimeInput {
  /** Bot's own remaining clock, in seconds. */
  remainingSeconds: number;
  /** How many moves the bot can choose from: the cheapest available proxy for complexity. */
  legalMoveCount: number;
  /** Bot strength in Elo, as passed on the game URL. */
  elo: number;
  /** Injectable for tests. Must return [0, 1). */
  random?: () => number;
}

/**
 * How long the bot should appear to think, in milliseconds.
 *
 * The shape: a fraction of the remaining clock, scaled by how complex the position is, jittered so
 * the cadence never reads as mechanical, then clamped by the tier's floor/ceiling and, last and
 * non-negotiable, by what the bot can actually afford.
 *
 * Note the interaction with whole-second charging: the server bills whole seconds, and since C-4-2
 * it carries the sub-second remainder into the same side's next turn. So a single fast reply still
 * shows as costing nothing, which is the intended read and mirrors a human blitzing out an obvious
 * recapture, but a run of them is no longer free: the remainders add up and land as a whole second
 * on a later ply. Budget in milliseconds here and let the server do the rounding.
 */
export function simulatedThinkMs(input: ThinkTimeInput): number {
  const { remainingSeconds, legalMoveCount, elo } = input;
  const rand = input.random ?? Math.random;
  const policy = tierForElo(elo).clock;

  // Complexity proxy: ~30 legal moves is an ordinary middlegame. Few moves (forced recapture,
  // king walking out of check) should be fast; a wide-open position earns more time.
  const complexity = clamp(legalMoveCount / 30, 0.45, 1.6);

  const budgetMs =
    Math.max(0, remainingSeconds) * policy.budgetFraction * complexity * 1000;

  // Jitter (0.65x - 1.35x) so consecutive moves never take the same time. A perfectly regular
  // reply interval is the single most obvious tell that you are playing a script.
  const jittered = budgetMs * (0.65 + rand() * 0.7);

  const clamped = clamp(jittered, policy.minThinkMs, policy.maxThinkMs);

  // Affordability, applied last so it overrides every other term: never think past the tier's
  // reserve, and never think the bot to death. A bot that flags itself mid-thought is a bug at the
  // strong tiers, and at the weak tiers it should still lose by *playing* badly, not by hanging.
  const spendableSeconds = Math.max(
    0,
    remainingSeconds - policy.reserveSeconds,
  );
  const affordableMs = Math.max(0, spendableSeconds * 1000);

  return Math.round(
    clamp(clamped, 0, Math.max(policy.minThinkMs, affordableMs)),
  );
}

/**
 * P-6-6. How much of the original think delay is still owed after the tab
 * returns. Takes the shorter of (a) leftover original wait and (b) a fresh
 * `simulatedThinkMs` from the bot's remaining clock now. Backgrounding charges
 * the server clock while the client timer is frozen, so (b) is what stops a
 * leftover multi-second wait from flagging the bot on unlock.
 *
 * UI-only. The C# simulator has no tab, so this is not ported to BotClock.cs.
 */
export function remainingThinkMsAfterWake(input: {
  originalThinkMs: number;
  elapsedMs: number;
  remainingSeconds: number;
  legalMoveCount: number;
  elo: number;
  random?: () => number;
}): number {
  const leftover = Math.max(0, input.originalThinkMs - input.elapsedMs);
  const fresh = simulatedThinkMs({
    remainingSeconds: input.remainingSeconds,
    legalMoveCount: input.legalMoveCount,
    elo: input.elo,
    random: input.random,
  });
  return Math.min(leftover, fresh);
}

/**
 * A think delay that can be shortened when the tab comes back (P-6-6).
 * `retarget(nextMs)` waits `nextMs` from the moment it is called, not from
 * construction. `retarget(0)` resolves immediately.
 */
export function createThinkDelay(
  ms: number,
  signal: { aborted: boolean },
): {
  promise: Promise<void>;
  retarget: (nextMs: number) => void;
} {
  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  let settled = false;
  let resolveFn = () => {};

  const settle = () => {
    if (settled) return;
    settled = true;
    if (timeoutId !== undefined) clearTimeout(timeoutId);
    resolveFn();
  };

  const promise = new Promise<void>((resolve) => {
    resolveFn = resolve;
    if (signal.aborted || ms <= 0) {
      settle();
      return;
    }
    timeoutId = setTimeout(settle, ms);
  });

  function retarget(nextMs: number) {
    if (settled || signal.aborted) return;
    if (timeoutId !== undefined) clearTimeout(timeoutId);
    timeoutId = undefined;
    if (nextMs <= 0) {
      settle();
      return;
    }
    timeoutId = setTimeout(settle, nextMs);
  }

  return { promise, retarget };
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

/** Resolves after `ms`, and early if `signal` aborts, so a finished game never waits on a bot. */
export function sleep(
  ms: number,
  signal?: { aborted: boolean },
): Promise<void> {
  return createThinkDelay(ms, signal ?? { aborted: false }).promise;
}
