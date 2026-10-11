import type { ReactNode } from "react";

interface EmptyStateProps {
  title: string;
  description?: string;
  children?: ReactNode;
  className?: string;
  /**
   * Heading level for the title. Defaults to 2, which is right when the page already renders its
   * own h1 above this. Pass 1 where the empty state IS the page: a not-found or error branch that
   * returns early, before the page's real h1 is ever rendered, would otherwise have no heading at
   * all. The visual treatment is identical either way.
   */
  headingLevel?: 1 | 2;
}

export function EmptyState({
  title,
  description,
  children,
  className = "",
  headingLevel = 2,
}: EmptyStateProps) {
  const Heading = headingLevel === 1 ? "h1" : "h2";
  return (
    <div
      className={`flex flex-col items-center text-center gap-3 ${className}`}
    >
      <Heading className="font-bold text-base text-white m-0">{title}</Heading>
      {description ? (
        <p className="text-sm text-muted max-w-[400px] m-0 leading-relaxed">
          {description}
        </p>
      ) : null}
      {children}
    </div>
  );
}
