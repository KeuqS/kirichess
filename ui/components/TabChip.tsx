"use client";

import type { ReactNode } from "react";

interface TabChipProps {
  active: boolean;
  onClick: () => void;
  children: ReactNode;
  /** Extra classes on the button, e.g. `shrink-0`. */
  className?: string;
}

/**
 * A tab or filter chip. Deliberately not a fifth CTA look: it is the existing `btn-primary` /
 * `btn-secondary` pair, which is how `/leaderboards` (a reference page) already renders its pool
 * tabs. The staff pages had grown a separate hand-rolled chip
 * (`px-4 py-2 rounded-lg text-sm font-semibold border transition-colors`) in nine places instead.
 */
export function TabChip({
  active,
  onClick,
  children,
  className = "",
}: TabChipProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={`px-4 py-2 text-sm ${active ? "btn-primary" : "btn-secondary"} ${className}`}
    >
      {children}
    </button>
  );
}
