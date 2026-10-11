import type { CSSProperties, ReactNode } from "react";

interface CardProps {
  children: ReactNode;
  className?: string;
  padding?: string;
  /** CSS custom property name (e.g. "--gain") to tint the border with, overriding the default. */
  accentColorVar?: string;
  style?: CSSProperties;
  onClick?: () => void;
}

export function Card({
  children,
  className = "",
  padding = "p-5",
  accentColorVar,
  style,
  onClick,
}: CardProps) {
  return (
    <div
      className={` border border-[var(--border)] bg-[var(--surface)] ${padding} ${className}`}
      style={{
        ...(accentColorVar ? { borderColor: `var(${accentColorVar})` } : {}),
        ...style,
      }}
      onClick={onClick}
      role={onClick ? "button" : undefined}
      tabIndex={onClick ? 0 : undefined}
      onKeyDown={
        onClick
          ? (e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                onClick();
              }
            }
          : undefined
      }
    >
      {children}
    </div>
  );
}
