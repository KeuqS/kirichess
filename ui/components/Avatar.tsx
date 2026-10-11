interface AvatarProps {
  label: string;
  src?: string | null;
  size?: "sm" | "lg";
  /**
   * Currently has no visual effect: `globals.css` squares every corner in the app with a global
   * `border-radius: 0 !important`, so both values render identically. Kept because callers express
   * the intent (`/profile` and `/u/[id]` ask for square) and it is what would drive the corner again
   * if that override were ever lifted. See `.claude/rules/styling.md` section 1.
   */
  shape?: "circle" | "square";
  className?: string;
  initialsClassName?: string;
}

export function Avatar({
  label,
  src,
  size = "sm",
  shape = "circle",
  className = "",
  initialsClassName = "",
}: AvatarProps) {
  const dims = size === "lg" ? "w-24 h-24 text-3xl" : "w-8 h-8 text-sm";
  void shape; // see the prop's doc comment: inert while corners are squared globally

  return (
    <div
      className={`${dims} bg-[var(--surface)] border border-[var(--border)] overflow-hidden flex items-center justify-center font-bold text-white transition-colors shrink-0 ${className}`}
    >
      {src ? (
        <img src={src} alt="" className="h-full w-full object-cover" />
      ) : (
        <span className={initialsClassName}>{label}</span>
      )}
    </div>
  );
}
