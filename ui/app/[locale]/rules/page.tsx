import type { Metadata } from "next";
import React from "react";
import { getLocale, getTranslations } from "next-intl/server";
import { routeMetadata } from "@/lib/seo";
import {
  IconArrowsExchange,
  IconFlame,
  IconCurrencyEuro,
} from "@tabler/icons-react";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { DataTable } from "@/components/DataTable";
import { PoolBadge } from "@/components/PoolBadge";
import { POOL_NAMES, PRESETS, type Economy } from "@/lib/presets";
import {
  CAPTURE_TABLE,
  CHECK_PROFIT_TABLE,
  CHIPS_ARE_UNIVERSAL,
  DEFAULT_RULES,
  DOC_MODEL,
  DURATION_TABLE,
  ENDGAME_TABLE,
  ENDING_RULES,
  POOL_ECONOMY_TABLE,
  POOL_TABLE,
  PRICED_EVENTS,
  THINKING_BUDGET_TABLE,
  TOLLS_ARE_UNIVERSAL,
  TOLL_TABLE,
  TRADE_MATH_TABLE,
} from "@/lib/rules.generated";

// M-1-1 / M-1-2: see about/page.tsx's comment - same fix, same reason. `/rules` is the page most
// likely to be linked when someone explains the variant, so it's the one M-1-1 names explicitly as
// worth getting right.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("rules"),
  ]);
  return routeMetadata({
    locale,
    path: "/rules",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

/**
 * `next-intl`'s `getTranslations` return type (includes `.rich`, used further down for the sentences
 * with an inline coloured `<span>`). Threaded through as a parameter, rather than each helper calling
 * `getTranslations` itself, because this page builds several data arrays before the component
 * renders, and a server component's `t` is only available inside the async component function.
 */
type RulesTranslator = Awaited<ReturnType<typeof getTranslations>>;

// ---------------------------------------------------------------------------------------------
// Every number on this page comes from config/kirichess.rules.json via `@/lib/rules.generated`,
// which the engine embeds verbatim. The tables below only reshape that generated data into the
// view models the DataTable columns expect: no rule value is written out by hand, so the manual
// cannot drift from the economy the server actually runs.
// ---------------------------------------------------------------------------------------------

const ECONOMY_TEXT_CLASS: Record<Economy, string> = {
  steal: "text-[var(--economy-steal)]",
  burn: "text-[var(--economy-burn)]",
  bounty: "text-[var(--economy-bounty)]",
};

// ---------------------------------------------------------------------------------------------
// Table cell tiers. Every DataTable on this page sizes and colours its cells from these three,
// the same grammar the leaderboard uses: the row's label, the one value the table exists to show,
// and everything supporting it. Colour is not a fourth tier - it layers onto a tier, and only for
// the two things on this page that a hue actually means:
//
//   * economy identity  - a cell that names steal/burn/bounty (ECONOMY_TEXT_CLASS, PoolBadge)
//   * a signed delta    - a number that can go up or down (deltaClass)
//
// Nothing else gets a hue. --bounty stays the brand accent (section numbers, links); a plain
// number is white, not gold, so that gold keeps meaning "steal" wherever it lands in a table.
// At most one column per table carries colour, so two different greens never share a row.
// ---------------------------------------------------------------------------------------------

/** The row's identity: the piece, the pool, the endgame. One per row, always first. */
const CELL_LABEL = "font-semibold text-white";
/** The value the table exists to show. One per row. */
const CELL_VALUE = "text-white font-medium";
/** Context around the value: breakdowns, secondary counts, comparisons. */
const CELL_SUPPORT = "text-sm text-muted";

/** Signed clock deltas: green up, red down, muted at zero. */
function deltaClass(delta: number): string {
  if (delta > 0) return "text-[var(--gain)]";
  if (delta < 0) return "text-[var(--loss)]";
  return "text-muted";
}

/** The base-scale dial label, e.g. "C1": what the capture table's numbers are quoted at. */
const BASE_MULTIPLIER = `C${DEFAULT_RULES.captureMult / DEFAULT_RULES.captureMult}`;

// Individual prices the prose quotes inline. Same source as the tables, so a retune rewrites the
// sentences too.
const CHIP = PRICED_EVENTS.checkGain;
const DOUBLE_CHIP = PRICED_EVENTS.doubleCheckLoss;
const PAWN_TOLL = PRICED_EVENTS.pawnToll;
const KING_TOLL = PRICED_EVENTS.kingToll;
const ROOK_TOLL = PRICED_EVENTS.rookToll;
const QUEEN_TOLL = DEFAULT_RULES.moveCostPerPiece.queen;
const CASTLE_COST = PRICED_EVENTS.castleCost;
const PAWN_CAPTURE = PRICED_EVENTS.pawnCapture;

/** What castling would cost if it were priced piece-by-piece instead of as a flat fee. */
const CASTLE_UNBUNDLED = KING_TOLL + ROOK_TOLL;

/** What the queen's toll would be if it were priced additively (rook + bishop) rather than discounted. */
const QUEEN_TOLL_UNDISCOUNTED =
  DEFAULT_RULES.moveCostPerPiece.rook + DEFAULT_RULES.moveCostPerPiece.bishop;

/** The toll bill per player over a typical game: identical in every pool while tolls don't scale. */
const TOLL_DRAIN = THINKING_BUDGET_TABLE[0].tollDrain;

/** The frequency-weighted average toll, and how much of it the heavy pieces carry. */
const EXACT_AVG_TOLL = (
  ["pawn", "knight", "bishop", "rook", "queen", "king"] as const
).reduce(
  (sum, piece) =>
    sum +
    DOC_MODEL.moveFrequency[piece] * DEFAULT_RULES.moveCostPerPiece[piece],
  0,
);
const AVG_TOLL = Math.round(EXACT_AVG_TOLL * 10) / 10;
const HEAVY_MOVE_SHARE = Math.round(
  (DOC_MODEL.moveFrequency.queen + DOC_MODEL.moveFrequency.rook) * 100,
);
const HEAVY_TOLL_SHARE = Math.round(
  ((DOC_MODEL.moveFrequency.queen * QUEEN_TOLL +
    DOC_MODEL.moveFrequency.rook * ROOK_TOLL) /
    EXACT_AVG_TOLL) *
    100,
);

/** Conversion bills by endgame name, for the sentences that quote one. */
const BILL = Object.fromEntries(
  ENDGAME_TABLE.map((e) => [e.name, e.bill]),
) as Record<string, number>;

const RATED_POOLS = POOL_TABLE.filter((p) => p.rated);

/** What an equal (knight-for-knight) trade moves at base scale: the trade table's own number. */
const EQUAL_TRADE = CAPTURE_TABLE.find((r) => r.piece === "knight")!.amount;

/** The best gap a single check can win, which is the pawn's: the cheapest piece that can give one. */
const BEST_CHECK_GAP = Math.max(...CHECK_PROFIT_TABLE.map((r) => r.gap));

/**
 * The First Edition's full-scale dial, kept only so the "what the old F4 pool felt like" comparison
 * in the design notes stays derived from today's toll bill rather than frozen at a stale percentage.
 */
const LEGACY_F4_SCALE = 4;

const POOL_ECONOMY = Object.fromEntries(
  POOL_ECONOMY_TABLE.map((p) => [p.key, p]),
) as Record<string, (typeof POOL_ECONOMY_TABLE)[number]>;

const POOL_DIAL = Object.fromEntries(
  POOL_TABLE.map((p) => [p.key, p.multiplier]),
) as Record<string, string>;

/**
 * Every number a pool's prose quotes about itself. Merged into the message values so a sentence
 * like "a lost queen is 100" interpolates the pool's real swing instead of stating it in en.json,
 * where a config retune would leave it behind. Callers pass the key of the pool being described.
 */
function poolNumbers(key: string): Record<string, number | string> {
  const e = POOL_ECONOMY[key];
  const budget = THINKING_BUDGET_TABLE.find((b) => b.key === key);
  return {
    minutes: e.minutes,
    dial: POOL_DIAL[key],
    chip: CHIP,
    perPoint: e.perPoint,
    pawnSwing: e.swing.pawn,
    bishopSwing: e.swing.bishop,
    rookSwing: e.swing.rook,
    queenSwing: e.swing.queen,
    queenAmount: e.amount.queen,
    queenSwingShare: e.queenSwingSharePct,
    tollShare: e.tollSharePct,
    tollShareAtF4: e.tollSharePct * LEGACY_F4_SCALE,
    scaledChip: CHIP * e.multiplierRatio,
    checksForQueen: e.checksToRecoverQueen,
    // The comeback comparisons are all against the base-multiplier steal pool. Pool keys are frozen
    // storage ids (Games.RatingPool), so naming one here does not move when a pool is renamed.
    baseChecksForQueen: POOL_ECONOMY.steal1.checksToRecoverQueen,
    // Rounded down, so the prose never claims more thinking time than the budget table shows.
    perMove: Math.floor(budget ? budget.perMove : 0),
    ceiling: DURATION_TABLE.find((d) => d.key === key)?.ceilingMinutes ?? 0,
  };
}

const NUMBER_WORDS = [
  "zero",
  "one",
  "two",
  "three",
  "four",
  "five",
  "six",
  "seven",
  "eight",
  "nine",
  "ten",
];
const RATED_POOL_COUNT =
  NUMBER_WORDS[RATED_POOLS.length] ?? String(RATED_POOLS.length);

/**
 * Steal pools running above the base capture multiplier: the ones where an average thinking
 * budget hides how unevenly the time actually lands once material falls.
 */
const HIGH_MULTIPLIER_STEAL = RATED_POOLS.filter(
  (p) =>
    p.economy === "steal" &&
    p.multiplier !== BASE_MULTIPLIER &&
    p.multiplier.startsWith("C"),
).map((p) => {
  const budget = THINKING_BUDGET_TABLE.find((b) => b.key === p.key);
  return `${p.label}'s "${budget?.perMove}s"`;
});

interface CaptureRow {
  icon: string;
  name: string;
  steal: string;
  burn: string;
  bounty: string;
}

const CAPTURE_ROWS: CaptureRow[] = CAPTURE_TABLE.map((r) => ({
  icon: r.glyph,
  name: r.label,
  steal: r.steal,
  burn: r.burn,
  bounty: r.bounty,
}));

interface TollRow {
  icon: string;
  name: string;
  toll: string;
}

const TOLL_ROWS: TollRow[] = TOLL_TABLE.map((r) => ({
  icon: r.glyph,
  name: r.label,
  toll: `${r.toll}s`,
}));

interface CheckProfitRow {
  piece: string;
  gap: string;
  gapColor: string;
}

const CHECK_PROFIT_ROWS: CheckProfitRow[] = CHECK_PROFIT_TABLE.map((r) => ({
  piece: r.piece,
  gap: r.gap === 0 ? "±0" : `${r.gap > 0 ? "+" : ""}${r.gap}`,
  gapColor: deltaClass(r.gap),
}));

interface TradeMathRow {
  economy: string;
  you: string;
  them: string;
  time: string;
  /** The hue rides the economy name, not the outcome: this row *is* an economy. */
  economyColor: string;
}

const TRADE_MATH_ROWS: TradeMathRow[] = TRADE_MATH_TABLE.map((r) => ({
  economy: r.label,
  you: r.you,
  them: r.them,
  time: r.effect,
  economyColor: ECONOMY_TEXT_CLASS[r.economy],
}));

interface EndingRow {
  name: string;
  description: string;
  verdict: string;
  verdictColor: string;
}

const WIN_COLOR = "text-[var(--gain)]";
const DRAW_COLOR = "text-muted";
const LOSS_COLOR = "text-[var(--loss)]";

// Each ending is gated on the flag that actually governs it, so turning a rule off in the config
// removes its row rather than leaving a lie on the page. Text is looked up by the translator that
// buildEndingRows() takes, since this file is a server component evaluated per-request.
function buildEndingRows(t: RulesTranslator): EndingRow[] {
  return [
    {
      name: t("endingCheckmateName"),
      description: t("endingCheckmateDescription"),
      verdict: t("verdictWin"),
      verdictColor: WIN_COLOR,
    },
    ...(ENDING_RULES.flagFallLoses
      ? [
          {
            name: t("endingFlagFallName"),
            description: ENDING_RULES.flagVsInsufficientMaterialIsDraw
              ? t("endingFlagFallDescriptionWithDraw")
              : t("endingFlagFallDescriptionNoDraw"),
            verdict: ENDING_RULES.flagVsInsufficientMaterialIsDraw
              ? t("verdictWinStar")
              : t("verdictWin"),
            verdictColor: WIN_COLOR,
          },
        ]
      : []),
    ...(ENDING_RULES.flagFallLoses &&
    ENDING_RULES.flagVsInsufficientMaterialIsDraw
      ? [
          {
            name: t("endingFlagVsBareKingName"),
            description: t("endingFlagVsBareKingDescription"),
            verdict: t("verdictDraw"),
            verdictColor: DRAW_COLOR,
          },
        ]
      : []),
    {
      name: t("endingStalemateName"),
      description: t("endingStalemateDescription"),
      verdict: t("verdictDraw"),
      verdictColor: DRAW_COLOR,
    },
    ...(ENDING_RULES.threefoldRepetitionIsDraw
      ? [
          {
            name: t("endingThreefoldName"),
            description: t("endingThreefoldDescription"),
            verdict: t("verdictDraw"),
            verdictColor: DRAW_COLOR,
          },
        ]
      : []),
    {
      name: t("endingFiftyMoveName"),
      description: t("endingFiftyMoveDescription"),
      verdict: t("verdictDraw"),
      verdictColor: DRAW_COLOR,
    },
    {
      name: t("endingInsufficientName"),
      description: t("endingInsufficientDescription"),
      verdict: t("verdictDraw"),
      verdictColor: DRAW_COLOR,
    },
    {
      name: t("endingAgreementName"),
      description: t("endingAgreementDescription"),
      verdict: t("verdictDraw"),
      verdictColor: DRAW_COLOR,
    },
    {
      name: t("endingResignationName"),
      description: t("endingResignationDescription"),
      verdict: t("verdictLoss"),
      verdictColor: LOSS_COLOR,
    },
    {
      name: t("endingAbandonedName"),
      description: t("endingAbandonedDescription"),
      verdict: t("verdictLoss"),
      verdictColor: LOSS_COLOR,
    },
    ...(ENDING_RULES.flagFallLoses
      ? [
          {
            name: t("endingTimeoutName"),
            description: ENDING_RULES.flagVsInsufficientMaterialIsDraw
              ? t("endingTimeoutDescriptionWithDraw")
              : t("endingTimeoutDescriptionNoDraw"),
            verdict: t("verdictLoss"),
            verdictColor: LOSS_COLOR,
          },
        ]
      : []),
  ];
}

interface ThinkingBudgetRow {
  pool: string;
  pool_s: string;
  drain: string;
  perMove: string;
  feel: string;
}

const THINKING_BUDGET_ROWS: ThinkingBudgetRow[] = THINKING_BUDGET_TABLE.map(
  (r) => ({
    pool: r.label,
    pool_s: `${r.poolSeconds}s`,
    // r.drain already carries its own "~" on each figure ("~81s tolls − ~75s bounty").
    drain: r.drain,
    perMove: `~${r.perMove}s`,
    feel: r.feelsLike,
  }),
);

interface DurationRow {
  pool: string;
  nominal: string;
  ceiling: string;
  beginner: string;
  intermediate: string;
  advanced: string;
}

const DURATION_ROWS: DurationRow[] = DURATION_TABLE.map((r) => ({
  pool: r.label,
  nominal: `${r.nominalMinutes} min`,
  ceiling: `~${r.ceilingMinutes} min`,
  beginner: r.beginner,
  intermediate: r.intermediate,
  advanced: r.advanced,
}));

interface MateRow {
  endgame: string;
  work: string;
  moves: string;
  bill: string;
}

const MATE_ROWS: MateRow[] = ENDGAME_TABLE.map((r) => ({
  endgame: r.name,
  work: r.work,
  moves: `~${r.moves}`,
  bill: `~${r.bill}s`,
}));

interface GlossaryRow {
  term: string;
  def: string;
}

function buildGlossaryRows(t: RulesTranslator): GlossaryRow[] {
  return [
    {
      term: t("glossaryClockGapTerm"),
      def: t("glossaryClockGapDef"),
    },
    {
      term: t("glossaryFlagfallTerm"),
      def: ENDING_RULES.flagVsInsufficientMaterialIsDraw
        ? t("glossaryFlagfallDef")
        : t("glossaryFlagfallDefNoDraw"),
    },
    {
      term: t("glossaryTollTerm"),
      def: t("glossaryTollDef"),
    },
    {
      term: t("glossaryChipTerm"),
      def: t("glossaryChipDef", {
        chip: CHIP,
        neverScales:
          TOLLS_ARE_UNIVERSAL && CHIPS_ARE_UNIVERSAL
            ? t("glossaryNeverScales")
            : "",
      }),
    },
    {
      term: t("glossaryStealBurnBountyTerm"),
      def: t("glossaryStealBurnBountyDef"),
    },
    {
      term: t("glossaryGuardTerm"),
      def: t("glossaryGuardDef"),
    },
    {
      term: t("glossaryCaptureMultiplierTerm"),
      def: t("glossaryCaptureMultiplierDef"),
    },
    {
      term: t("glossaryFModeTerm"),
      def: t("glossaryFModeDef"),
    },
    {
      term: t("glossaryThinkingBudgetTerm"),
      def: t("glossaryThinkingBudgetDef"),
    },
    {
      term: t("glossarySelfFinancingTerm"),
      def: t("glossarySelfFinancingDef"),
    },
    {
      term: t("glossaryConversionBillTerm"),
      def: t("glossaryConversionBillDef", {
        kq: BILL["K+Q vs K"],
        kr: BILL["K+R vs K"],
        everyPool: TOLLS_ARE_UNIVERSAL ? t("glossaryInEveryPool") : "",
      }),
    },
    {
      term: t("glossaryDeadCapitalTerm"),
      def: t("glossaryDeadCapitalDef"),
    },
  ];
}

const POOL_ROWS = POOL_TABLE.map((p) => ({
  key: p.key,
  label: p.label,
  time: p.time,
  scale: p.multiplier,
  economy: p.economy,
}));

/** Piece glyph + name; w-5 keeps ⟲ on the same origin as the chess glyphs. */
function PieceGlyphLabel({ icon, name }: { icon: string; name: string }) {
  return (
    <span className={`flex items-center gap-2 ${CELL_LABEL}`}>
      <span className="inline-flex w-5 shrink-0 justify-center text-xl leading-none">
        {icon}
      </span>
      <span>{name}</span>
    </span>
  );
}

/**
 * Editorial notes on each pool. The time control, economy and multiplier are *not* written here:
 * they are read off `POOL_TABLE` at render time, so a pool that gets retuned can never end up with
 * a stale "5+0 - Steal - C2" label above prose describing something else.
 */
interface PoolDetail {
  key: string;
  title: string;
  paragraphs: string[];
  rewards: string;
  punishes: string;
}

// A pool name lives in config/kirichess.rules.json and reaches this page through POOL_TABLE. It
// is never written out here, and the prose in en.json takes it as a `{bounty1}` / `{steal1}`
// placeholder rather than baking the word in, so renaming a pool stays a one-line config edit.
// The message keys stay on the pool's `key`, which is a frozen storage id (it is the value in
// Games.RatingPool) and does not move with the name. Names themselves are never translated: they
// are the variant's proper nouns.
function buildPoolDetails(t: RulesTranslator): PoolDetail[] {
  return RATED_POOLS.map((pool) => ({
    key: pool.key,
    title: pool.label,
    paragraphs: [
      t(`${pool.key}Para1`, { ...POOL_NAMES, ...poolNumbers(pool.key) }),
      t(`${pool.key}Para2`, { ...POOL_NAMES, ...poolNumbers(pool.key) }),
    ],
    rewards: t(`${pool.key}Rewards`, POOL_NAMES),
    punishes: t(`${pool.key}Punishes`, POOL_NAMES),
  }));
}

interface TocLink {
  href: string;
  label: string;
}

interface TocGroup extends TocLink {
  children: TocLink[];
}

function buildTocGroups(
  t: RulesTranslator,
  poolDetails: PoolDetail[],
): TocGroup[] {
  return [
    {
      href: "#concept",
      label: t("tocConcept"),
      children: [
        { href: "#changes", label: t("tocChanges") },
        { href: "#unchanged", label: t("tocUnchanged") },
      ],
    },
    {
      href: "#rules",
      label: t("tocRules"),
      children: [
        { href: "#captures", label: t("tocCaptures") },
        { href: "#tolls", label: t("tocTolls") },
        { href: "#check", label: t("tocCheck") },
        { href: "#repetition", label: t("tocRepetition") },
        { href: "#priced", label: t("tocPriced") },
        { href: "#ending", label: t("tocEnding") },
      ],
    },
    {
      href: "#economy",
      label: t("tocEconomy"),
      children: [
        { href: "#victory", label: t("tocVictory") },
        { href: "#trade-math", label: t("tocTradeMath") },
        { href: "#queen-cost", label: t("tocQueenCost") },
      ],
    },
    {
      href: "#pools",
      label: t("tocPools", { count: RATED_POOL_COUNT }),
      children: poolDetails.map((p) => ({
        href: `#pool-${p.key}`,
        label: p.title,
      })),
    },
    {
      href: "#tempo",
      label: t("tocTempo"),
      children: [
        { href: "#budget", label: t("tocBudget") },
        { href: "#duration", label: t("tocDuration") },
      ],
    },
    {
      href: "#design",
      label: t("tocDesign"),
      children: [
        { href: "#capture-only", label: t("tocCaptureOnly") },
        { href: "#promotion-free", label: t("tocPromotionFree") },
        { href: "#open-dials", label: t("tocOpenDials") },
      ],
    },
    {
      href: "#glossary",
      label: t("tocGlossary"),
      children: [],
    },
  ];
}

export default async function Rules() {
  const t = await getTranslations("rules");
  const endingRows = buildEndingRows(t);
  const glossaryRows = buildGlossaryRows(t);
  const poolDetails = buildPoolDetails(t);
  const tocGroups = buildTocGroups(t, poolDetails);

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <div className="flex-1 flex items-start">
        {/* TOC SIDEBAR - in-flow, sticky within the page */}
        <aside className="hidden lg:block w-56 shrink-0 sticky top-24 h-[calc(100vh-6rem)] overflow-y-auto border-r border-[var(--border)]/30 bg-[var(--background)]">
          <div className="px-8 pt-10 pb-10">
            <div className="text-sm tracking-widest text-muted mb-4">
              {t("tocSidebarHeading")}
            </div>
            <nav className="flex flex-col gap-2 text-sm font-medium">
              {tocGroups.map((group) => (
                <div key={group.href} className="flex flex-col">
                  <a
                    href={group.href}
                    className="text-[var(--foreground)]/90 hover:text-[var(--bounty)] py-1 transition-colors"
                  >
                    {group.label}
                  </a>
                  {group.children.length > 0 && (
                    <div className="flex flex-col border-l border-[var(--border)]/30 ml-1 pl-3 mt-0.5">
                      {group.children.map((child) => (
                        <a
                          key={child.href}
                          href={child.href}
                          className="text-sm text-muted hover:text-[var(--bounty)] py-1 transition-colors"
                        >
                          {child.label}
                        </a>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </nav>
          </div>
        </aside>

        {/* MAIN CONTENT - CENTERED IN REMAINING SPACE */}
        <main
          id="main-content"
          className="flex-1 min-w-0 page-prose pt-10 pb-32 scroll-mt-24"
        >
          {/* CONCEPT */}
          <section id="concept" className="mb-16 scroll-mt-24">
            <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
              {t("conceptH1")}
            </h1>
            <h2
              id="changes"
              className="text-xl font-bold text-white mb-4 mt-10 tracking-tight scroll-mt-24"
            >
              {t("changesHeading")}
            </h2>
            <div className="flex flex-col gap-5 mb-10">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("changesPara1")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("changesPara2")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("changesPara3")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("changesPara4")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("changesPara5")}
              </p>
            </div>

            <h2
              id="unchanged"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("unchangedHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("unchangedPara1")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("unchangedPara2")}
              </p>
            </div>
          </section>

          {/* RULES */}
          <section
            id="rules"
            className="mb-16 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <h2 className="text-2xl font-bold mb-4 tracking-tight text-white">
              {t("rulesHeading")}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] mb-10 leading-relaxed">
              {t("rulesIntro")}
            </p>

            {/* 1 CAPTURES ARE TIME */}
            <article id="captures" className="scroll-mt-24 mb-14">
              <div className="flex items-baseline gap-3 mb-4">
                <span className="text-xl font-bold text-[var(--bounty)]">
                  01
                </span>
                <h3 className="text-xl font-bold m-0 tracking-tight text-white">
                  {t("capturesH3")}
                </h3>
              </div>
              <p className="text-[15px] text-[var(--foreground)] mb-8 leading-relaxed">
                {t("capturesIntro")}
              </p>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="flex items-center gap-2 mb-3">
                    <IconArrowsExchange
                      size={18}
                      stroke={2.25}
                      className="text-[var(--economy-steal)]"
                    />
                    <span className="text-sm uppercase tracking-widest text-[var(--economy-steal)]">
                      {t("stealLabel")}
                    </span>
                  </div>
                  <div className="text-sm text-[var(--foreground)] leading-relaxed">
                    {t.rich("stealCardBody", {
                      gains: (chunks) => (
                        <span className="text-white">{chunks}</span>
                      ),
                      loses: (chunks) => (
                        <span className="text-white">{chunks}</span>
                      ),
                    })}
                  </div>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="flex items-center gap-2 mb-3">
                    <IconCurrencyEuro
                      size={18}
                      stroke={2.25}
                      className="text-[var(--economy-bounty)]"
                    />
                    <span className="text-sm uppercase tracking-widest text-[var(--economy-bounty)]">
                      {t("bountyLabel")}
                    </span>
                  </div>
                  <div className="text-sm text-[var(--foreground)] leading-relaxed">
                    {t.rich("bountyCardBody", {
                      gains: (chunks) => (
                        <span className="text-white">{chunks}</span>
                      ),
                    })}
                  </div>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="flex items-center gap-2 mb-3">
                    <IconFlame
                      size={18}
                      stroke={2.25}
                      className="text-[var(--economy-burn)]"
                    />
                    <span className="text-sm uppercase tracking-widest text-[var(--economy-burn)]">
                      {t("burnLabel")}
                    </span>
                  </div>
                  <div className="text-sm text-[var(--foreground)] leading-relaxed">
                    {t.rich("burnCardBody", {
                      loses: (chunks) => (
                        <span className="text-white">{chunks}</span>
                      ),
                    })}
                  </div>
                </div>
              </div>

              {/* capture table */}
              <DataTable
                columns={[
                  {
                    header: t("captureTableColPiece"),
                    render: (r: CaptureRow) => (
                      <PieceGlyphLabel icon={r.icon} name={r.name} />
                    ),
                  },
                  {
                    header: t("stealLabel"),
                    align: "right",
                    render: (r: CaptureRow) => (
                      <span className="font-medium text-[var(--economy-steal)]">
                        {r.steal}
                      </span>
                    ),
                  },
                  {
                    header: t("bountyLabel"),
                    align: "right",
                    render: (r: CaptureRow) => (
                      <span className="font-medium text-[var(--economy-bounty)]">
                        {r.bounty}
                      </span>
                    ),
                  },
                  {
                    header: t("burnLabel"),
                    align: "right",
                    render: (r: CaptureRow) => (
                      <span className="font-medium text-[var(--economy-burn)]">
                        {r.burn}
                      </span>
                    ),
                  },
                ]}
                rows={CAPTURE_ROWS}
                rowKey={(r) => r.name}
                gridTemplateColumns="1.4fr 1fr 1fr 1fr"
              />
              <p className="text-sm text-muted mt-5 leading-relaxed">
                {t.rich("captureFootnote", {
                  multiplier: BASE_MULTIPLIER,
                  poolCount: POOL_TABLE.length,
                  pools: (chunks) => (
                    <a
                      href="#pools"
                      className="underline hover:text-[var(--bounty)] transition-colors"
                    >
                      {chunks}
                    </a>
                  ),
                })}
              </p>
            </article>

            {/* 2 TOLLS ON POWER */}
            <article id="tolls" className="scroll-mt-24 mb-14">
              <div className="flex items-baseline gap-3 mb-4">
                <span className="text-xl font-bold text-[var(--bounty)]">
                  02
                </span>
                <h3 className="text-xl font-bold m-0 tracking-tight text-white">
                  {t("tollsH3")}
                </h3>
              </div>
              <p className="text-[15px] text-[var(--foreground)] mb-8 leading-relaxed">
                {t("tollsIntro")}
              </p>

              <DataTable
                columns={[
                  {
                    header: t("tollTableColPiece"),
                    render: (r: TollRow) => (
                      <PieceGlyphLabel icon={r.icon} name={r.name} />
                    ),
                  },
                  {
                    header: t("tollTableColToll"),
                    align: "right",
                    render: (r: TollRow) => (
                      <span className={CELL_VALUE}>{r.toll}</span>
                    ),
                  },
                ]}
                rows={TOLL_ROWS}
                rowKey={(r) => r.name}
                gridTemplateColumns="1.6fr 1fr"
              />

              <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mt-8">
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="text-base text-white mb-2">
                    {t("tollsNeverScaleTitle")}
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {t("tollsNeverScaleBody")}
                  </p>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="text-base text-white mb-2">
                    {t("castlingDiscountTitle")}
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {t("castlingDiscountBody", {
                      kingToll: KING_TOLL,
                      rookToll: ROOK_TOLL,
                      unbundled: CASTLE_UNBUNDLED,
                      castleCost: CASTLE_COST,
                    })}
                  </p>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="text-base text-white mb-2">
                    {t("zugzwangTitle")}
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {t("zugzwangBody")}
                  </p>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="text-base text-white mb-2">
                    {t("avgMoveCostTitle", { avgToll: AVG_TOLL })}
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {t("avgMoveCostBody", {
                      tollDrain: TOLL_DRAIN,
                      movesPerPlayer: DOC_MODEL.movesPerPlayer,
                      heavyMoveShare: HEAVY_MOVE_SHARE,
                      heavyTollShare: HEAVY_TOLL_SHARE,
                    })}
                  </p>
                </div>
              </div>
            </article>

            {/* 3 CHECK CHIP */}
            <article id="check" className="mb-14 scroll-mt-24">
              <div className="flex items-baseline gap-3 mb-4">
                <span className="text-xl font-bold text-[var(--bounty)]">
                  03
                </span>
                <h3 className="text-xl font-bold m-0 tracking-tight text-white">
                  {t("checkChipH3")}
                </h3>
              </div>
              <p className="text-[15px] text-[var(--foreground)] mb-4 leading-relaxed">
                {t("checkChipIntro", {
                  chip: CHIP,
                  multiplier: DEFAULT_RULES.doubleCheckChipMultiplier,
                  doubleChip: DOUBLE_CHIP,
                })}
              </p>
              <p className="text-sm text-muted mb-8 bg-[var(--surface)] inline-block px-3 py-1.5 border border-[var(--border)]">
                {t("checkChipFormula", { chip: CHIP })}
              </p>

              {/* profitability table */}
              <DataTable
                columns={[
                  {
                    header: t("checkTableColCheckBy"),
                    render: (r: CheckProfitRow) => (
                      <span className={CELL_LABEL}>{r.piece}</span>
                    ),
                  },
                  {
                    header: t("checkTableColGapSwing"),
                    align: "right",
                    render: (r: CheckProfitRow) => (
                      <span className={`font-medium ${r.gapColor}`}>
                        {r.gap}
                      </span>
                    ),
                  },
                ]}
                rows={CHECK_PROFIT_ROWS}
                rowKey={(r) => r.piece}
                gridTemplateColumns="1.6fr 1fr"
              />
              <p className="text-[15px] text-[var(--foreground)] mt-5 leading-relaxed">
                {t("checkChipReadNote")}
              </p>
            </article>

            {/* 4 REPETITION GUARD */}
            <article id="repetition" className="mb-14 scroll-mt-24">
              <div className="flex items-baseline gap-3 mb-4">
                <span className="text-xl font-bold text-[var(--bounty)]">
                  04
                </span>
                <h3 className="text-xl font-bold m-0 tracking-tight text-white">
                  {t("repetitionH3")}
                </h3>
              </div>
              <p className="text-[15px] text-[var(--foreground)] mb-6 leading-relaxed">
                {t("repetitionIntro")}
              </p>
              <ul className="text-[15px] text-[var(--foreground)] leading-relaxed list-disc pl-5 space-y-2 mb-4">
                <li>{t("repetitionLi1")}</li>
                <li>{t("repetitionLi2")}</li>
                <li>{t("repetitionLi3")}</li>
              </ul>
              <p className="text-sm text-muted mt-5 leading-relaxed">
                {t("repetitionFootnote")}
              </p>
            </article>

            {/* OTHER PRICED EVENTS */}
            <article id="priced" className="mb-14 scroll-mt-24">
              <h3 className="text-xl font-bold m-0 mb-4 tracking-tight text-white">
                {t("otherEventsH3")}
              </h3>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5 transition-colors">
                  <div className="flex items-center gap-2 mb-3">
                    <span className="text-2xl leading-none">♟→♛</span>
                    <span className="text-base text-white">
                      {PRICED_EVENTS.promotionIsFree
                        ? t("promotionFreeTitle")
                        : t("promotionPricedTitle")}
                    </span>
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {PRICED_EVENTS.promotionIsFree
                      ? t.rich("promotionFreeBody", {
                          pawnToll: PAWN_TOLL,
                          toll: (chunks) => (
                            <span className="text-[var(--bounty)] px-1 rounded bg-[var(--bounty)]/10">
                              {chunks}
                            </span>
                          ),
                        })
                      : t.rich("promotionPricedBody", {
                          pawnToll: PAWN_TOLL,
                          mult: DEFAULT_RULES.promotionCostMult,
                          queenCost: PRICED_EVENTS.promotionQueenCost,
                          cost: (chunks) => (
                            <span className="text-[var(--bounty)] px-1 rounded bg-[var(--bounty)]/10">
                              {chunks}
                            </span>
                          ),
                        })}
                  </p>
                </div>
                <div className="border border-[var(--border)] bg-[var(--surface)] p-5 transition-colors">
                  <div className="flex items-center gap-2 mb-3">
                    <span className="text-2xl leading-none">♟×♟</span>
                    <span className="text-base text-white">
                      {t("enPassantTitle")}
                    </span>
                  </div>
                  <p className="text-sm text-[var(--foreground)] m-0 leading-relaxed">
                    {t.rich("enPassantBody", {
                      pawnCapture: PAWN_CAPTURE,
                      val: (chunks) => (
                        <span className="text-[var(--economy-steal)] px-1 rounded bg-[var(--economy-steal)]/10">
                          {chunks}
                        </span>
                      ),
                    })}
                  </p>
                </div>
              </div>
            </article>

            {/* HOW GAMES END */}
            <article id="ending" className="mb-0 scroll-mt-24">
              <h3 className="text-xl font-bold m-0 mb-4 tracking-tight text-white">
                {t("endingH3")}
              </h3>
              <div className="border border-[var(--border)] bg-[var(--surface)]">
                {endingRows.map((r, i) => (
                  <div
                    key={r.name}
                    className={`flex flex-col gap-1 px-5 py-3 ${
                      i < endingRows.length - 1
                        ? "border-b border-[var(--border)]/50"
                        : ""
                    }`}
                  >
                    <div className="flex items-baseline justify-between gap-4">
                      <span className={`text-base ${CELL_LABEL}`}>
                        {r.name}
                      </span>
                      <span className={`text-sm shrink-0 ${r.verdictColor}`}>
                        {r.verdict}
                      </span>
                    </div>
                    <p className="text-sm text-[var(--foreground)] leading-relaxed m-0">
                      {r.description}
                    </p>
                  </div>
                ))}
              </div>
              <p className="text-sm text-muted mt-5 leading-relaxed">
                {t("endingFootnote", POOL_NAMES)}
              </p>
            </article>
          </section>

          {/* ECONOMY DEEP DIVE */}
          <section
            id="economy"
            className="mb-16 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <h2 className="text-2xl font-bold mb-4 tracking-tight text-white">
              {t("economyH2")}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] mb-10 leading-relaxed">
              {t("economyIntro")}
            </p>

            <h3
              id="victory"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("victoryH3")}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] mb-6 leading-relaxed">
              {t("victoryIntro")}
            </p>
            <DataTable
              columns={[
                {
                  header: t("mateTableColEndgame"),
                  render: (r: MateRow) => (
                    <span className="flex min-w-0 flex-col gap-0.5">
                      <span className={CELL_LABEL}>{r.endgame}</span>
                      <span className={CELL_SUPPORT}>{r.work}</span>
                    </span>
                  ),
                },
                {
                  header: t("mateTableColMoves"),
                  align: "right",
                  render: (r: MateRow) => (
                    <span className={CELL_SUPPORT}>{r.moves}</span>
                  ),
                },
                {
                  header: t("mateTableColBill"),
                  align: "right",
                  render: (r: MateRow) => (
                    <span className={CELL_VALUE}>{r.bill}</span>
                  ),
                },
              ]}
              rows={MATE_ROWS}
              rowKey={(r) => r.endgame}
              gridTemplateColumns="minmax(0,1fr) 5rem 5.5rem"
            />
            <p className="text-sm text-muted mt-5 mb-8">
              {t("mateTableFootnote")}
            </p>
            <p className="text-[15px] text-[var(--foreground)] mb-4 leading-relaxed">
              {t("conversionBillPara", {
                ...POOL_NAMES,
                bill: BILL["K+R vs K"],
              })}
            </p>
            <ol className="text-[15px] text-[var(--foreground)] leading-relaxed list-decimal pl-5 space-y-2 mb-14">
              <li>{t("mateLi1")}</li>
              <li>{t("mateLi2")}</li>
              <li>{t("mateLi3")}</li>
            </ol>

            <h3
              id="trade-math"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("tradeMathH3")}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] mb-6 leading-relaxed">
              {t("tradeMathIntro")}
            </p>
            <DataTable
              columns={[
                {
                  header: t("tradeMathColEconomy"),
                  render: (r: TradeMathRow) => (
                    <span className={`font-semibold ${r.economyColor}`}>
                      {r.economy}
                    </span>
                  ),
                },
                {
                  header: t("tradeMathColYou"),
                  align: "right",
                  render: (r: TradeMathRow) => (
                    <span className={CELL_SUPPORT}>{r.you}</span>
                  ),
                },
                {
                  header: t("tradeMathColThem"),
                  align: "right",
                  render: (r: TradeMathRow) => (
                    <span className={CELL_SUPPORT}>{r.them}</span>
                  ),
                },
                {
                  header: t("tradeMathColGap"),
                  align: "right",
                  render: (r: TradeMathRow) => (
                    <span className={CELL_VALUE}>{r.time}</span>
                  ),
                },
              ]}
              rows={TRADE_MATH_ROWS}
              rowKey={(r) => r.economy}
              gridTemplateColumns="1.8fr 1.8fr 0.55fr 1.1fr"
            />
            <p className="text-sm text-muted mt-5 mb-14 leading-relaxed">
              {t("tradeMathFootnote", { equalTrade: EQUAL_TRADE })}
            </p>

            <h3
              id="queen-cost"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("queenCostH3", {
                queenToll: QUEEN_TOLL,
                undiscounted: QUEEN_TOLL_UNDISCOUNTED,
              })}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              {t("queenCostPara", {
                undiscounted: QUEEN_TOLL_UNDISCOUNTED,
                rookToll: ROOK_TOLL,
                bishopToll: DEFAULT_RULES.moveCostPerPiece.bishop,
                tenXUndiscounted: 10 * QUEEN_TOLL_UNDISCOUNTED,
                queenToll: QUEEN_TOLL,
              })}
            </p>
          </section>

          {/* POOLS */}
          <section
            id="pools"
            className="mb-16 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <h2 className="text-2xl font-bold m-0 mb-4 tracking-tight text-white">
              {t("poolsH2", { count: RATED_POOL_COUNT })}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] mb-10 leading-relaxed">
              {t("poolsIntro")}
            </p>
            <DataTable
              columns={[
                {
                  header: t("poolTableColPool"),
                  render: (r: (typeof POOL_ROWS)[number]) => (
                    <span className={CELL_LABEL}>{r.label}</span>
                  ),
                },
                {
                  header: t("poolTableColTime"),
                  align: "right",
                  render: (r: (typeof POOL_ROWS)[number]) => (
                    <span className={CELL_VALUE}>{r.time}</span>
                  ),
                },
                {
                  header: t("poolTableColMultiplier"),
                  align: "right",
                  render: (r: (typeof POOL_ROWS)[number]) => (
                    <span className={CELL_SUPPORT}>{r.scale}</span>
                  ),
                },
                {
                  header: t("poolTableColEconomy"),
                  align: "right",
                  render: (r: (typeof POOL_ROWS)[number]) => (
                    <PoolBadge
                      economy={r.economy}
                      variant="plain"
                      className="text-white"
                    />
                  ),
                },
              ]}
              rows={POOL_ROWS}
              rowKey={(r) => r.key}
              gridTemplateColumns="1.1fr 0.8fr 0.7fr 0.9fr"
            />
            <p className="text-sm text-muted mt-5 mb-10">
              {t("poolTableFootnote")}
            </p>

            <div className="flex flex-col gap-6">
              {poolDetails.map((p) => {
                const preset = PRESETS[p.key as keyof typeof PRESETS];
                // "6+0 - Steal - C1", read off the live config rather than written beside the prose.
                const tag = preset.tag;
                return (
                  <div
                    key={p.key}
                    id={`pool-${p.key}`}
                    className="border border-[var(--border)] bg-[var(--surface)] p-6 scroll-mt-24"
                  >
                    <div className="flex items-center gap-3 flex-wrap">
                      <h3 className="text-xl font-bold text-white m-0 tracking-tight">
                        {p.title}
                      </h3>
                      <span className="text-sm text-muted tracking-wide">
                        {tag}
                      </span>
                    </div>
                    <div className="flex flex-col gap-4 mt-4 mb-4">
                      {p.paragraphs.map((para, i) => (
                        <p
                          key={i}
                          className="text-[15px] text-[var(--foreground)] m-0 leading-relaxed"
                        >
                          {para}
                        </p>
                      ))}
                    </div>
                    <div className="flex flex-wrap gap-x-8 gap-y-1 text-sm pt-3 border-t border-[var(--border)]/60">
                      <span className="text-white">
                        <strong className="font-normal">
                          {t("rewardsLabel")}
                        </strong>{" "}
                        <span className="text-muted">{p.rewards}</span>
                      </span>
                      <span className="text-white">
                        <strong className="font-normal">
                          {t("punishesLabel")}
                        </strong>{" "}
                        <span className="text-muted">{p.punishes}</span>
                      </span>
                    </div>
                  </div>
                );
              })}
            </div>
          </section>

          {/* TEMPO AND DURATION */}
          <section
            id="tempo"
            className="mb-16 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <div className="flex items-center gap-3 mb-4">
              <h2 className="text-2xl font-bold m-0 tracking-tight text-white">
                {t("tempoH2")}
              </h2>
            </div>

            <h3
              id="budget"
              className="text-xl font-bold text-white mb-4 mt-10 tracking-tight scroll-mt-24"
            >
              {t("budgetH3")}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] mb-6 leading-relaxed">
              {t("budgetIntro", { moves: DOC_MODEL.movesPerPlayer })}
            </p>
            <DataTable
              columns={[
                {
                  header: t("budgetTableColPool"),
                  render: (r: ThinkingBudgetRow) => (
                    <span className={CELL_LABEL}>{r.pool}</span>
                  ),
                },
                {
                  header: t("budgetTableColPoolPlayer"),
                  align: "right",
                  render: (r: ThinkingBudgetRow) => (
                    <span className={CELL_SUPPORT}>{r.pool_s}</span>
                  ),
                },
                {
                  header: t("budgetTableColEconomyDrain"),
                  render: (r: ThinkingBudgetRow) => (
                    <span className={CELL_SUPPORT}>{r.drain}</span>
                  ),
                },
                {
                  header: t("budgetTableColThinkingMove"),
                  align: "right",
                  render: (r: ThinkingBudgetRow) => (
                    <span className={CELL_VALUE}>{r.perMove}</span>
                  ),
                },
                {
                  header: t("budgetTableColFeelsLike"),
                  align: "right",
                  render: (r: ThinkingBudgetRow) => (
                    <span className={CELL_SUPPORT}>{r.feel}</span>
                  ),
                },
              ]}
              rows={THINKING_BUDGET_ROWS}
              rowKey={(r) => r.pool}
              gridTemplateColumns="1fr 1fr 1.6fr 1fr 1fr"
            />
            <ul className="text-[15px] text-[var(--foreground)] leading-relaxed list-disc pl-5 space-y-2 mt-6 mb-14">
              <li>{t("thinkingBudgetLi1", { tollDrain: TOLL_DRAIN })}</li>
              <li>
                {t("thinkingBudgetLi2", {
                  ladder: THINKING_BUDGET_TABLE.map((r) => r.perMove).join(
                    " → ",
                  ),
                })}
              </li>
              <li>
                {t("thinkingBudgetLi3", {
                  pools: HIGH_MULTIPLIER_STEAL.join(" and "),
                })}
              </li>
              <li>{t("thinkingBudgetLi4", POOL_NAMES)}</li>
            </ul>

            <h3
              id="duration"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("durationH3")}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] mb-6 leading-relaxed">
              {t("durationIntro")}
            </p>
            <DataTable
              columns={[
                {
                  header: t("durationTableColPool"),
                  render: (r: DurationRow) => (
                    <span className={CELL_LABEL}>{r.pool}</span>
                  ),
                },
                {
                  header: t("durationTableColNominal"),
                  align: "right",
                  render: (r: DurationRow) => (
                    <span className={CELL_SUPPORT}>{r.nominal}</span>
                  ),
                },
                {
                  // The effective ceiling, not the nominal max, is the answer this table exists
                  // to give: it is the one a player plans an evening around.
                  header: t("durationTableColCeiling"),
                  align: "right",
                  render: (r: DurationRow) => (
                    <span className={CELL_VALUE}>{r.ceiling}</span>
                  ),
                },
                {
                  header: t("durationTableColBeginner"),
                  align: "right",
                  render: (r: DurationRow) => (
                    <span className={CELL_SUPPORT}>{r.beginner}</span>
                  ),
                },
                {
                  header: t("durationTableColIntermediate"),
                  align: "right",
                  render: (r: DurationRow) => (
                    <span className={CELL_SUPPORT}>{r.intermediate}</span>
                  ),
                },
                {
                  header: t("durationTableColAdvanced"),
                  align: "right",
                  render: (r: DurationRow) => (
                    <span className={CELL_SUPPORT}>{r.advanced}</span>
                  ),
                },
              ]}
              rows={DURATION_ROWS}
              rowKey={(r) => r.pool}
              gridTemplateColumns="1fr 1fr 1.2fr 1fr 1fr 1fr"
            />
            <ul className="text-[15px] text-[var(--foreground)] leading-relaxed list-disc pl-5 space-y-2 mt-6 mb-0">
              <li>{t("durationLi1", POOL_NAMES)}</li>
              <li>{t("durationLi2", POOL_NAMES)}</li>
              <li>{t("durationLi3", POOL_NAMES)}</li>
              <li>{t("durationLi4", POOL_NAMES)}</li>
            </ul>
          </section>

          {/* DESIGN NOTES */}
          <section
            id="design"
            className="mb-16 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <div className="flex items-center gap-3 mb-4">
              <h2 className="text-2xl font-bold m-0 tracking-tight text-white">
                {t("designH2")}
              </h2>
            </div>

            <h3
              id="capture-only"
              className="text-xl font-bold text-white mb-4 mt-10 tracking-tight scroll-mt-24"
            >
              {t("captureOnlyH3")}
            </h3>
            <div className="flex flex-col gap-5 mb-14">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("captureOnlyPara1", POOL_NAMES)}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("captureOnlyPara2", {
                  pawnCheck: BEST_CHECK_GAP,
                  kq: BILL["K+Q vs K"],
                  kr: BILL["K+R vs K"],
                })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("captureOnlyPara3")}
              </p>
            </div>

            <h3
              id="promotion-free"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("promotionFreeH3")}
            </h3>
            <p className="text-[15px] text-[var(--foreground)] mb-14 leading-relaxed">
              {t("promotionFreePara")}
            </p>

            <h3
              id="open-dials"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("openDialsH3")}
            </h3>
            <div className="flex flex-col gap-5">
              <div className="border border-[var(--border)] bg-[var(--surface)] p-5 text-sm text-[var(--foreground)] leading-relaxed">
                {t.rich("openDial1", {
                  ...POOL_NAMES,
                  ...poolNumbers("steal3"),
                  strong: (chunks) => (
                    <strong className="text-white">{chunks}</strong>
                  ),
                })}
              </div>
              <div className="border border-[var(--border)] bg-[var(--surface)] p-5 text-sm text-[var(--foreground)] leading-relaxed">
                {t.rich("openDial2", {
                  ...POOL_NAMES,
                  ...poolNumbers("steal2"),
                  strong: (chunks) => (
                    <strong className="text-white">{chunks}</strong>
                  ),
                })}
              </div>
              <div className="border border-[var(--border)] bg-[var(--surface)] p-5 text-sm text-[var(--foreground)] leading-relaxed">
                {t.rich("openDial3", {
                  ...POOL_NAMES,
                  ...poolNumbers("steal3"),
                  strong: (chunks) => (
                    <strong className="text-white">{chunks}</strong>
                  ),
                })}
              </div>
            </div>
            <p className="text-sm text-muted mt-5 leading-relaxed">
              {t("designFootnote")}
            </p>
          </section>

          {/* GLOSSARY */}
          <section
            id="glossary"
            className="mb-5 pt-16 border-t border-[var(--border)] scroll-mt-24"
          >
            <div className="flex items-center gap-3 mb-4">
              <h2 className="text-2xl font-bold m-0 tracking-tight text-white">
                {t("glossaryH2")}
              </h2>
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
              {glossaryRows.map((g) => (
                <div
                  key={g.term}
                  className="border border-[var(--border)] bg-[var(--surface)] p-5"
                >
                  <div className={`text-base mb-2 ${CELL_LABEL}`}>{g.term}</div>
                  <div className="text-sm text-muted leading-relaxed">
                    {g.def}
                  </div>
                </div>
              ))}
            </div>
          </section>

          {/* LICENCE, CC BY 4.0 on this manual's text. Apache-2.0 covers the
              platform source (NOTICE); do not put Apache on the rules wording. */}
          <section
            id="licence"
            className="pt-10 border-t border-[var(--border)] scroll-mt-24"
          >
            <h2 className="text-sm font-bold m-0 mb-3 tracking-wide text-muted">
              {t("licenceH2")}
            </h2>
            <p className="text-sm text-muted leading-relaxed max-w-2xl">
              {t("licenceBody")} {t("licenceAttribution")}{" "}
              <a
                href="https://creativecommons.org/licenses/by/4.0/"
                target="_blank"
                rel="noopener noreferrer license"
                className="underline hover:text-[var(--bounty)] transition-colors"
              >
                {t("licenceLinkLabel")}
              </a>
              .
            </p>
          </section>
        </main>
      </div>
      <Footer />
    </div>
  );
}
