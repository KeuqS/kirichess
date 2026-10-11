"use client";

import {
  IconArrowsExchange,
  IconFlame,
  IconCurrencyEuro,
  type Icon,
} from "@tabler/icons-react";
import { useTranslations } from "next-intl";
import type { Economy } from "@/lib/presets";

const ECONOMY_STYLE: Record<
  Economy,
  { icon: Icon; text: string; bg: string; border: string }
> = {
  steal: {
    icon: IconArrowsExchange,
    text: "text-[var(--economy-steal)]",
    bg: "bg-[var(--economy-steal)]/10",
    border: "border-[var(--economy-steal)]/30",
  },
  burn: {
    icon: IconFlame,
    text: "text-[var(--economy-burn)]",
    bg: "bg-[var(--economy-burn)]/10",
    border: "border-[var(--economy-burn)]/30",
  },
  bounty: {
    icon: IconCurrencyEuro,
    text: "text-[var(--economy-bounty)]",
    bg: "bg-[var(--economy-bounty)]/10",
    border: "border-[var(--economy-bounty)]/30",
  },
};

interface PoolBadgeProps {
  economy: Economy;
  /** Optional time control suffix, e.g. "3+0". */
  detail?: string;
  /** "pill" = bordered chip (Profile); "plain" = glyph + label only (Home, Rules table). */
  variant?: "pill" | "plain";
  className?: string;
}

export function PoolBadge({
  economy,
  detail,
  variant = "pill",
  className = "",
}: PoolBadgeProps) {
  const t = useTranslations("PoolBadge");
  const label: Record<Economy, string> = {
    steal: t("steal"),
    burn: t("burn"),
    bounty: t("bounty"),
  };
  const meta = ECONOMY_STYLE[economy];
  const Glyph = meta.icon;
  const text = detail
    ? `${label[economy].toUpperCase()} ${detail}`
    : label[economy].toUpperCase();

  if (variant === "plain") {
    return (
      <span
        className={`inline-flex items-center gap-2 text-sm tracking-widest font-semibold ${className || meta.text}`}
      >
        <Glyph size={18} stroke={2.25} />
        {text}
      </span>
    );
  }

  return (
    <span
      className={`inline-flex items-center gap-1 text-sm tracking-widest px-2 py-1 rounded border font-bold ${meta.text} ${meta.bg} ${meta.border} ${className}`}
    >
      <Glyph size={12} stroke={2.5} />
      {text}
    </span>
  );
}
