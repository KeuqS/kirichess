"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Dialog } from "@/components/Dialog";
import { ApiError, submitReport } from "@/lib/api";
import type { ReportReason } from "@/lib/types";

const REASON_LABEL_KEY: Record<ReportReason, string> = {
  EngineAssistance: "reasonEngineAssistance",
  Sandbagging: "reasonSandbagging",
  Stalling: "reasonStalling",
  Abuse: "reasonAbuse",
  Other: "reasonOther",
};

const REASONS = Object.keys(REASON_LABEL_KEY) as ReportReason[];
const DETAILS_MAX = 1000;

interface ReportDialogProps {
  reportedUserId: string;
  gameId: string | null;
  onClose: () => void;
}

/**
 * Phase 6e.2 report intake modal. Submits to `/api/reports` and shows a short confirmation: the
 * report is stored, not acted on (moderation tooling is Phase 8.5), so there is nothing further to
 * show the reporter beyond "we got it."
 */
export function ReportDialog({
  reportedUserId,
  gameId,
  onClose,
}: ReportDialogProps) {
  const t = useTranslations("report");
  const [reason, setReason] = useState<ReportReason>(REASONS[0]);
  const [details, setDetails] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(false);

  async function handleSubmit() {
    setError(null);
    setSubmitting(true);
    try {
      await submitReport({
        reportedUserId,
        gameId,
        reason,
        details: details.trim() === "" ? null : details.trim(),
      });
      setSubmitted(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("reportFailed"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Dialog title={t("reportPlayer")} onClose={onClose}>
      {submitted ? (
        <>
          <p className="mt-4 text-sm text-[var(--foreground)]/80">
            {t("reportSubmitted")}
          </p>
          <div className="mt-6 flex justify-end">
            <button onClick={onClose} className="btn-primary px-5 py-2 text-sm">
              {t("close")}
            </button>
          </div>
        </>
      ) : (
        <>
          <label className="mt-4 block text-sm text-muted">
            {t("reason")}
            <select
              value={reason}
              onChange={(e) => setReason(e.target.value as ReportReason)}
              className="input-select mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 pr-10 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
            >
              {REASONS.map((r) => (
                <option key={r} value={r}>
                  {t(REASON_LABEL_KEY[r])}
                </option>
              ))}
            </select>
          </label>

          <label className="mt-4 block text-sm text-muted">
            {t("detailsOptional")}
            <textarea
              value={details}
              onChange={(e) => setDetails(e.target.value.slice(0, DETAILS_MAX))}
              rows={4}
              className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
            />
            <span className="mt-1 block text-right text-sm text-muted">
              {details.length}/{DETAILS_MAX}
            </span>
          </label>

          {error && <p className="mt-2 text-sm text-[var(--loss)]">{error}</p>}

          <div className="mt-6 flex justify-end gap-3">
            <button
              onClick={onClose}
              className="btn-secondary px-3 py-1.5 text-sm"
            >
              {t("cancel")}
            </button>
            <button
              onClick={handleSubmit}
              disabled={submitting}
              className="btn-primary px-3 py-1.5 text-sm"
            >
              {submitting ? t("submitting") : t("submit")}
            </button>
          </div>
        </>
      )}
    </Dialog>
  );
}
