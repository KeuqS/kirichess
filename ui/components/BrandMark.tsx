import type { ReactNode } from "react";
import { Link } from "@/i18n/navigation";
import { IconChessKing, IconChessKingFilled } from "@tabler/icons-react";

/** Bounty-colored king used next to the KiriChess wordmark. */
export function BrandMark({
  size = 24,
  solidOnHover = false,
}: {
  size?: number;
  solidOnHover?: boolean;
}) {
  const outline = (
    <IconChessKing
      size={size}
      stroke={2}
      className={
        solidOnHover
          ? "brand-mark-outline text-[var(--bounty)] transition-opacity"
          : "shrink-0 text-[var(--bounty)]"
      }
      aria-hidden
    />
  );

  if (!solidOnHover) return outline;

  return (
    <span className="relative inline-flex shrink-0">
      {outline}
      <IconChessKingFilled
        size={size}
        className="brand-mark-solid pointer-events-none absolute inset-0 text-[var(--bounty)] opacity-0 transition-opacity"
        aria-hidden
      />
    </span>
  );
}

/** Wordmark on chrome-less auth cards (login, reset, verify, …). A home link, not a heading; each screen supplies its own h1. */
export function AuthBrandHeading({ children }: { children: ReactNode }) {
  return (
    <p className="mb-6 flex items-center justify-center">
      <Link
        href="/"
        className="flex items-center justify-center gap-3 text-center text-2xl font-bold tracking-widest text-white transition-colors hover:text-[var(--bounty)] hover:[&_.brand-mark-outline]:opacity-0 hover:[&_.brand-mark-solid]:opacity-100"
      >
        <BrandMark size={28} solidOnHover />
        {children}
      </Link>
    </p>
  );
}
