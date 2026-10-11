"use client";

import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { MiniBoard } from "@/components/MiniBoard";
import { PatronIcon } from "@/components/PatronIcon";
import { PIECE_BLACK, PIECE_WHITE } from "@/lib/boardTheme";
import { PRESETS, isPresetKey } from "@/lib/presets";
import type { LiveGameDto, Side } from "@/lib/types";

interface LiveGameCardProps {
  game: LiveGameDto;
  /**
   * Display seconds, already interpolated by the page's single ticker. Passed in rather than
   * derived here so one interval drives every card and this component stays presentational.
   */
  whiteSeconds: number;
  blackSeconds: number;
  /**
   * Destination of the card. Defaults to the live spectate page. Pass `null` for a placeholder
   * that has no game behind it: same look, no navigation.
   */
  href?: string | null;
}

/** Local formatter: `Clock`'s `formatTime` is private and this file must not export it either. */
function formatClock(totalSeconds: number): string {
  const clamped = Math.max(0, Math.floor(totalSeconds));
  const minutes = Math.floor(clamped / 60);
  const seconds = clamped % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

/**
 * Piece-colour chip next to a name. Decorative: the card's accessible name already says which
 * player is which side. Fill matches MiniBoard's piece tints, not the square pair - a blue-grey
 * square next to the board reads as a square, not as Black.
 */
function SideSwatch({ side }: { side: Side }) {
  const isWhite = side === "White";
  return (
    <span
      aria-hidden="true"
      className={`inline-block size-3.5 shrink-0 border ${
        isWhite ? "border-black/70" : "border-white/55"
      }`}
      style={{ backgroundColor: isWhite ? PIECE_WHITE : PIECE_BLACK }}
    />
  );
}

function ClockRow({
  name,
  tier,
  seconds,
  active,
  side,
}: {
  name: string;
  tier: string;
  seconds: number;
  active: boolean;
  side: Side;
}) {
  const flagged = seconds <= 0;

  return (
    <div className="flex items-center justify-between gap-3">
      <span className="flex min-w-0 items-center gap-1.5">
        <SideSwatch side={side} />
        <span
          className={`truncate text-base ${
            active ? "font-semibold text-[var(--bounty)]" : "text-muted"
          }`}
        >
          {name}
        </span>
        <PatronIcon tier={tier} size={16} className="shrink-0" />
      </span>
      <span
        className={`shrink-0 font-mono text-base tabular-nums ${
          flagged
            ? "text-[var(--loss)]"
            : active
              ? "font-semibold text-[var(--bounty)]"
              : "text-muted"
        }`}
      >
        {formatClock(seconds)}
      </span>
    </div>
  );
}

const CARD_CLASS =
  "group flex flex-col gap-4 border border-[var(--border)] bg-[var(--surface)] p-4 no-underline text-inherit transition-all duration-200 hover:border-[var(--bounty)]/50 hover:shadow-[0_0_16px_-4px_var(--bounty)]";

/**
 * One live game in the /watch grid: the position, both clocks, and which pool it is being played
 * in. The whole card is the link, so the board needs no interaction of its own.
 */
export function LiveGameCard({
  game,
  whiteSeconds,
  blackSeconds,
  href,
}: LiveGameCardProps) {
  const t = useTranslations("watch");
  const preset =
    game.pool && isPresetKey(game.pool) ? PRESETS[game.pool] : null;
  const label = t("watchRowAria", {
    white: game.whiteName,
    black: game.blackName,
    whiteSide: t("white"),
    blackSide: t("black"),
  });

  const body = (
    <>
      {game.fen ? (
        <MiniBoard fen={game.fen} />
      ) : (
        // No position on this entry: a read-model published by an instance older than the field.
        // Substituting the start position would be actively wrong for a game already underway, so
        // the card renders without a board rather than lying about one.
        <div className="aspect-square w-full border border-[var(--border)] bg-[var(--background)]" />
      )}

      <div className="flex flex-col gap-1">
        <ClockRow
          name={game.blackName}
          tier={game.blackPatronTier}
          seconds={blackSeconds}
          active={game.sideToMove === "Black"}
          side="Black"
        />
        <ClockRow
          name={game.whiteName}
          tier={game.whitePatronTier}
          seconds={whiteSeconds}
          active={game.sideToMove === "White"}
          side="White"
        />
      </div>

      <div className="flex items-center justify-between gap-2">
        <span className="truncate text-base text-muted">
          {preset ? preset.label : t("customLabel")}
        </span>
        <span className="shrink-0 text-base text-muted tabular-nums">
          {t("moveCount", { count: game.moveCount })}
        </span>
      </div>
    </>
  );

  if (href === null) {
    return (
      <div className={CARD_CLASS} aria-label={label}>
        {body}
      </div>
    );
  }

  return (
    <Link
      href={href ?? `/watch/${game.gameId}`}
      aria-label={label}
      className={CARD_CLASS}
    >
      {body}
    </Link>
  );
}
