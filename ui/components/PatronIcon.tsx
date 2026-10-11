"use client";

import { useTranslations } from "next-intl";
import { IconHeart, IconHeartFilled } from "@tabler/icons-react";

/**
 * Known API tier strings (`PatronStatusDto.tier`, `LeaderboardEntryDto.patronTier`, …).
 * "None" and anything else are absent: `PatronIcon` renders nothing for those. The values
 * still map a Stripe amount to a grant on the server; they are not shown in the UI.
 *
 * The public badge is one heart in `--bounty` for every tier: a piece glyph next to a
 * rating reads as strength, and a named ladder on `/donate` is the same ranking with a
 * price list. Outline is a one-off; filled is monthly.
 */
export const PATRON_TIERS = [
  "Pawn",
  "Knight",
  "Bishop",
  "Rook",
  "Queen",
] as const;

export type PatronTierName = (typeof PATRON_TIERS)[number];

export function isPatronTier(tier?: string | null): tier is PatronTierName {
  return !!tier && (PATRON_TIERS as readonly string[]).includes(tier);
}

interface PatronIconProps {
  /** Raw API tier string. Anything other than a known tier (including "None") renders nothing. */
  tier?: string | null;
  /**
   * "OneTime" -> outline, "Monthly" -> filled. Absent -> outline: the public DTOs
   * (`LeaderboardEntryDto`, `LiveGameDto`, `SpectatorSnapshotDto`) carry a tier but no kind.
   */
  kind?: string | null;
  size?: number;
  className?: string;
}

/**
 * A patron thank-you, decorative next to a name that is already text. One heart in `--bounty`
 * for every paid tier: outline for a one-off, filled for monthly. Renders nothing for "None",
 * for an unrecognised string, and for undefined/null, so every call site can render it
 * unconditionally with no guard of its own.
 *
 * Accessibility: the wrapping `<span>` carries `role="img"` and the accessible label; the `<svg>`
 * itself is `aria-hidden`. The label is just "Patron" (`DonationTiers.patronBadgeLabel`), never
 * the tier name, so a screen reader does not hear a price tag a sighted user cannot see.
 */
export function PatronIcon({
  tier,
  kind,
  size = 16,
  className,
}: PatronIconProps) {
  const t = useTranslations("DonationTiers");
  if (!isPatronTier(tier)) return null;

  const Icon = kind === "Monthly" ? IconHeartFilled : IconHeart;

  return (
    <span role="img" aria-label={t("patronBadgeLabel")} className={className}>
      <Icon
        size={size}
        stroke={2}
        className="text-[var(--bounty)]"
        aria-hidden="true"
      />
    </span>
  );
}
