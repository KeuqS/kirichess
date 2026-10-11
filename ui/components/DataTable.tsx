import type { ReactNode } from "react";
import { Link } from "@/i18n/navigation";

interface Column<T> {
  header: string;
  align?: "left" | "right";
  render: (row: T) => ReactNode;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  /** CSS grid-template-columns for both the header and body rows, e.g. "1.4fr 1fr 1fr 1fr". */
  gridTemplateColumns: string;
  showHeader?: boolean;
  rowKey: (row: T, index: number) => string | number;
  rowClassName?: (row: T, index: number) => string;
  /** When set, each row is a link. Prefer this over a button-in-row for list navigation. */
  rowHref?: (row: T, index: number) => string;
  rowAriaLabel?: (row: T, index: number) => string;
}

function alignClass(
  align: Column<unknown>["align"],
  forHeader: boolean,
): string {
  if (align === "right") {
    return forHeader ? "text-right" : "text-right tabular-nums";
  }
  if (align === "left") return "text-left";
  return "";
}

export function DataTable<T>({
  columns,
  rows,
  gridTemplateColumns,
  showHeader = true,
  rowKey,
  rowClassName,
  rowHref,
  rowAriaLabel,
}: DataTableProps<T>) {
  return (
    <div
      className="border border-[var(--border)] bg-[var(--surface)] overflow-x-auto"
      tabIndex={0}
    >
      {/* One grid for the whole table so `fr` tracks are shared. Separate per-row
          grids with min-w-max sized `fr` columns independently, so a long name
          shoved Rating/Clocks off the header. Subgrid rows keep hover, links,
          and padding on the row while inheriting those tracks. */}
      <div className="grid min-w-full gap-x-2" style={{ gridTemplateColumns }}>
        {showHeader && (
          <div className="col-span-full grid grid-cols-subgrid items-center px-5 py-3 border-b border-[var(--border)] table-header bg-[var(--background)]">
            {columns.map((col, i) => (
              <span key={i} className={alignClass(col.align, true)}>
                {col.header}
              </span>
            ))}
          </div>
        )}
        {rows.map((row, i) => {
          const className = `col-span-full grid grid-cols-subgrid items-center px-5 py-3 no-underline text-inherit ${
            i < rows.length - 1 ? "border-b border-[var(--border)]/50" : ""
          } ${rowHref ? "hover:bg-[var(--border)]/30 cursor-pointer" : ""} ${
            rowClassName ? rowClassName(row, i) : ""
          }`;
          const cells = columns.map((col, j) => (
            <div key={j} className={`min-w-0 ${alignClass(col.align, false)}`}>
              {col.render(row)}
            </div>
          ));

          if (rowHref) {
            return (
              <Link
                key={rowKey(row, i)}
                href={rowHref(row, i)}
                aria-label={rowAriaLabel?.(row, i)}
                className={className}
              >
                {cells}
              </Link>
            );
          }

          return (
            <div key={rowKey(row, i)} className={className}>
              {cells}
            </div>
          );
        })}
      </div>
    </div>
  );
}
