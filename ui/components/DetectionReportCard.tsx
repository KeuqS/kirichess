"use client";

import { useRef, useState } from "react";
import { IconAlertTriangle, IconChartLine } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { EmptyState } from "@/components/EmptyState";
import { SkeletonRows } from "@/components/SkeletonRows";
import { ApiError } from "@/lib/api";
import type { DetectionReportDto, DetectionVerdictName } from "@/lib/types";

/** Exported so the sweep list tints its rows from the same source rather than a second copy. */
export const VERDICT_COLOR: Record<DetectionVerdictName, string> = {
  Insufficient: "var(--border)",
  Unremarkable: "var(--gain)",
  Watch: "var(--bounty)",
  Elevated: "var(--loss)",
};

const FOOTER_NOTE =
  "Advisory only. This score has never been validated against a confirmed cheat and is not, by itself, grounds for a sanction.";

function sampleLabel(report: DetectionReportDto): string {
  const unit =
    report.detector === "engine-assistance" ? "scored plies" : "games";
  return `from ${report.sampleSize} ${unit}`;
}

function VerdictChip({ verdict }: { verdict: DetectionVerdictName }) {
  const color = VERDICT_COLOR[verdict];
  return (
    <span
      className="inline-flex items-center gap-1.5 border px-3 py-1 text-sm font-bold tracking-wide"
      style={{
        color,
        borderColor: color,
        backgroundColor: `color-mix(in srgb, ${color} 12%, transparent)`,
      }}
    >
      {verdict}
    </span>
  );
}

function ReportBody({ report }: { report: DetectionReportDto }) {
  const color = VERDICT_COLOR[report.verdict];
  const showScore = report.verdict !== "Insufficient";

  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-center justify-between flex-wrap gap-3">
        <div className="flex items-center gap-3">
          <VerdictChip verdict={report.verdict} />
          {report.pool && (
            <span className="text-sm text-muted">{report.pool}</span>
          )}
        </div>
        <span className="text-sm text-muted">
          computed {new Date(report.computedAt).toLocaleString()}
        </span>
      </div>

      {showScore ? (
        <div>
          <div className="text-3xl font-bold text-white">
            {(report.score * 100).toFixed(0)}%
          </div>
          <div className="text-sm text-muted mt-1">{sampleLabel(report)}</div>
        </div>
      ) : (
        // Showing "0%" next to Insufficient reads as "clean": the exact misreading the verdict
        // vocabulary exists to prevent. No score bar at all in this state.
        <div className="text-sm text-muted">
          Not enough data to score this player ({sampleLabel(report)}).
        </div>
      )}

      {/* Caveats: always visible, directly under the score, never behind a tooltip or accordion. */}
      {report.caveats.length > 0 && (
        <div className="flex flex-col gap-2 border border-[var(--loss)]/30 bg-[var(--loss)]/10 p-3">
          {report.caveats.map((caveat, i) => (
            <div
              key={i}
              className="flex items-start gap-2 text-sm text-[var(--loss)]"
            >
              <IconAlertTriangle
                size={14}
                stroke={2}
                className="mt-0.5 shrink-0"
              />
              <span>{caveat}</span>
            </div>
          ))}
        </div>
      )}

      {report.signals.length > 0 && (
        <div className="flex flex-col gap-4">
          {report.signals.map((signal) => (
            <div key={signal.name} className="flex flex-col gap-1.5">
              <div className="flex items-center justify-between gap-3">
                <span className="text-sm font-semibold text-white">
                  {signal.label}
                </span>
                <span className="text-sm text-muted">value {signal.value}</span>
              </div>
              <div className="h-1.5 w-full bg-[var(--border)]/40 overflow-hidden">
                <div
                  className="h-full "
                  style={{
                    width: `${Math.round(signal.contribution * 100)}%`,
                    backgroundColor: color,
                  }}
                />
              </div>
              {/* Never truncated: this sentence is the working, and hiding it defeats the point. */}
              <p className="text-sm text-muted leading-relaxed m-0">
                {signal.evidence}
              </p>
            </div>
          ))}
        </div>
      )}

      <p className="text-sm text-muted leading-relaxed border-t border-[var(--border)] pt-3 m-0">
        {FOOTER_NOTE}
      </p>
    </div>
  );
}

interface DetectionReportCardProps {
  title: string;
  /** Fetches the report(s) for this detector. Always an array: sandbagging can return one per pool. */
  fetchReports: () => Promise<DetectionReportDto[]>;
}

type Status = "idle" | "loading" | "not-deployed" | "error" | "loaded";

/**
 * Renders the shared detection envelope (README's pinned `DetectionReportDto`), used for both the
 * sandbagging detector (Task B) and the engine-assistance detector (Task C): one component,
 * rendered twice, because the wire contract is identical.
 *
 * Loads lazily behind an explicit button: the engine analysis deserializes up to 200 jsonb move
 * lists server-side, and a moderator opening a dossier to read a report should not trigger that by
 * accident. Tasks B and C are being built in parallel and their routes do not exist yet in every
 * environment: a 404 or 501 renders "This detector is not deployed yet." as an expected state,
 * never a red error.
 */
export function DetectionReportCard({
  title,
  fetchReports,
}: DetectionReportCardProps) {
  const [status, setStatus] = useState<Status>("idle");
  const [reports, setReports] = useState<DetectionReportDto[]>([]);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // C-10-10: this card is rendered twice per dossier (`title`/`fetchReports` are dossier-specific
  // closures the caller re-creates per render) and its fetch is triggered by a click rather than an
  // effect, so a MatchHistoryCard-style `cancelled` boolean on mount is not enough - the guard has
  // to compare the `fetchReports` in effect *when the response lands* against the one captured
  // *when the request started*. If the caller has since re-pointed this card at a different report
  // (props changed under it, e.g. a moderator opening a different dossier without this instance
  // unmounting), a slow response from the old target must not render under the new heading.
  const fetchReportsRef = useRef(fetchReports);
  fetchReportsRef.current = fetchReports;

  function run() {
    const requestedFetch = fetchReports;
    setStatus("loading");
    setErrorMessage(null);
    requestedFetch()
      .then((data) => {
        if (fetchReportsRef.current !== requestedFetch) return;
        setReports(data);
        setStatus("loaded");
      })
      .catch((err) => {
        if (fetchReportsRef.current !== requestedFetch) return;
        if (
          err instanceof ApiError &&
          (err.status === 404 || err.status === 501)
        ) {
          setStatus("not-deployed");
          return;
        }
        setErrorMessage(
          err instanceof ApiError ? err.message : "Analysis failed.",
        );
        setStatus("error");
      });
  }

  return (
    <Card padding="p-6">
      <div className="flex items-center justify-between gap-3 mb-4">
        <h3 className="flex items-center gap-2 text-sm font-bold text-white tracking-wide">
          <IconChartLine
            size={18}
            stroke={2}
            className="text-[var(--bounty)]"
          />
          {title}
        </h3>
        {status !== "idle" && status !== "loading" && (
          <button
            onClick={run}
            className="text-sm font-semibold text-muted hover:text-[var(--bounty)] transition-colors"
          >
            Re-run
          </button>
        )}
      </div>

      {status === "idle" && (
        <EmptyState
          title="Not yet run"
          description="This analysis is computed on demand: it is not run automatically when the dossier loads."
        >
          <button onClick={run} className="btn-primary mt-2 px-5 py-2 text-sm">
            Run analysis
          </button>
        </EmptyState>
      )}

      {status === "loading" && <SkeletonRows count={4} />}

      {status === "not-deployed" && (
        <EmptyState
          title="Not deployed yet"
          description="This detector is not deployed yet."
        />
      )}

      {status === "error" && (
        <EmptyState
          title="Couldn't run analysis"
          description={errorMessage ?? "Something went wrong."}
        />
      )}

      {status === "loaded" &&
        (reports.length === 0 ? (
          <EmptyState
            title="No report returned"
            description="The detector returned no data for this player."
          />
        ) : (
          <div className="flex flex-col gap-6">
            {reports.map((report, i) => (
              <div key={`${report.pool ?? "all"}-${i}`}>
                {i > 0 && (
                  <div className="border-t border-[var(--border)] mb-6" />
                )}
                <ReportBody report={report} />
              </div>
            ))}
          </div>
        ))}
    </Card>
  );
}
