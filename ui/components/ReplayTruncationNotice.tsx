"use client";

import { useTranslations } from "next-intl";

/**
 * C-8-3. A corrupt move log is served as a prefix rather than a 500, and the stored result still
 * describes the finish on the row. Without this notice the page draws that result over a movetext
 * that never reaches it.
 */
export function ReplayTruncationNotice({
  truncatedAtPly,
}: {
  truncatedAtPly: number | null | undefined;
}) {
  const t = useTranslations("replay");
  // `== null` also covers a response that omits the field. Absence and null both mean the movetext
  // is complete; only a number (including 0, the unplayable start) is a truncation.
  if (truncatedAtPly == null) return null;

  return (
    <div className="notice-banner mb-4" role="status">
      {truncatedAtPly === 0
        ? t("incompleteStart")
        : t("incompleteRecord", { ply: truncatedAtPly })}
    </div>
  );
}
