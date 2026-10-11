import { Avatar } from "@/components/Avatar";

interface Patron {
  name: string;
}

interface PatronWallProps {
  patrons: readonly Patron[];
  heading: string;
  intro?: string;
  listLabel: string;
}

/**
 * Even-grid wall of patrons. The donate page feeds dummy names today; a live list
 * can reuse the same component once Stripe-backed display names exist.
 */
export function PatronWall({
  patrons,
  heading,
  intro,
  listLabel,
}: PatronWallProps) {
  return (
    <section className="mt-16 pt-16 border-t border-[var(--border)]">
      <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
        {heading}
      </h2>
      {intro ? (
        <p className="text-sm text-[var(--foreground)] leading-relaxed mb-8 max-w-2xl">
          {intro}
        </p>
      ) : null}
      <ul
        aria-label={listLabel}
        className="grid grid-cols-2 gap-x-8 gap-y-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5"
      >
        {patrons.map((patron) => (
          <li key={patron.name} className="flex min-w-0 items-center gap-2.5">
            <Avatar label={patron.name.slice(0, 2).toUpperCase()} />
            <span className="min-w-0 truncate text-sm font-medium text-white">
              {patron.name}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
