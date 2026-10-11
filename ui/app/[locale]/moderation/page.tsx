"use client";

import { useEffect, useState } from "react";
import { Link, useRouter } from "@/i18n/navigation";
import {
  IconAlertTriangle,
  IconChartLine,
  IconClockOff,
  IconFlag,
  IconGavel,
  IconPencil,
  IconSearch,
  IconUser,
} from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { DataTable } from "@/components/DataTable";
import { Dialog } from "@/components/Dialog";
import { EmptyState } from "@/components/EmptyState";
import { TabChip } from "@/components/TabChip";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { RequireAuth } from "@/components/RequireAuth";
import { StaffOnly } from "@/components/ForbiddenScreen";
import { SkeletonRows } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import { VERDICT_COLOR } from "@/components/DetectionReportCard";
import {
  ApiError,
  getDisplayNameScan,
  getDormantAccounts,
  getEngineGameReport,
  getModerationReports,
  getSanctions,
  getSandbaggingSweep,
  getTwoFactorStatus,
  liftSanction,
  moderatorRenameDisplayName,
  resolveModerationReport,
} from "@/lib/api";
import type {
  DetectionReportDto,
  DisplayNameScanRowDto,
  DormancyReason,
  DormantAccountRowDto,
  DormantAccountScanDto,
  ModerationReportDto,
  ModerationReportStatus,
  SanctionDto,
  SanctionTypeName,
} from "@/lib/types";

type Tab = "reports" | "sanctions" | "sweep" | "names" | "lookup" | "retention";
type StatusFilter = ModerationReportStatus | "all";

const STATUS_FILTERS: StatusFilter[] = ["Open", "Reviewed", "Dismissed", "all"];

const SANCTION_TINT: Record<SanctionTypeName, string> = {
  Warning: "var(--bounty)",
  RatedPlayBan: "var(--accent)",
  AccountBan: "var(--loss)",
};

const DORMANCY_TINT: Record<DormancyReason, string> = {
  "never-activated": "var(--loss)",
  "never-played": "var(--bounty)",
  inactive: "var(--foreground)",
};

const DETAILS_TRUNCATE = 200;

/** "2h ago" / "3d ago" / "just now". */
function timeAgo(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const minutes = Math.floor(ms / 60000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function countColor(count: number): string {
  if (count >= 4) return "var(--loss)";
  if (count >= 2) return "var(--bounty)";
  return "var(--foreground)";
}

function TabButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <TabChip active={active} onClick={onClick} className="flex gap-2">
      {icon}
      {label}
    </TabChip>
  );
}

function ReportsTab() {
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("Open");
  const [reports, setReports] = useState<ModerationReportDto[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [actionModal, setActionModal] = useState<{
    report: ModerationReportDto;
    action: ModerationReportStatus;
  } | null>(null);
  const [gameAnalysis, setGameAnalysis] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    getModerationReports(
      statusFilter === "all" ? undefined : { status: statusFilter },
    )
      .then((data) => {
        if (!cancelled) setReports(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof ApiError ? err.message : "Failed to load reports.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [statusFilter]);

  function toggleExpand(id: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function submitAction(moderatorNote: string) {
    if (!actionModal || !reports) return;
    const { report, action } = actionModal;
    const previous = reports;
    // Optimistically remove the row when the active filter is "Open": it no longer belongs there.
    if (statusFilter === "Open") {
      setReports(reports.filter((r) => r.id !== report.id));
    }
    setActionModal(null);
    try {
      await resolveModerationReport(report.id, {
        status: action,
        moderatorNote:
          moderatorNote.trim() === "" ? undefined : moderatorNote.trim(),
      });
    } catch (err) {
      // Restore the row and surface the failure.
      setReports(previous);
      setError(
        err instanceof ApiError ? err.message : "Failed to resolve report.",
      );
    }
  }

  return (
    <>
      <div className="flex gap-2 mb-6 flex-wrap">
        {STATUS_FILTERS.map((s) => (
          <TabChip
            key={s}
            active={statusFilter === s}
            onClick={() => setStatusFilter(s)}
          >
            {s === "all" ? "All" : s}
          </TabChip>
        ))}
      </div>

      {error && <div className="error-banner mb-4">{error}</div>}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={5} />
        </Card>
      ) : !reports || reports.length === 0 ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No reports here"
            description="Nothing matches this filter right now."
          />
        </Card>
      ) : (
        <DataTable
          gridTemplateColumns="1.3fr 1fr 1fr 80px 90px 170px"
          rows={reports}
          rowKey={(row) => row.id}
          columns={[
            {
              header: "REPORTED PLAYER",
              render: (row) => (
                <Link
                  href={`/moderation/${row.reportedUserId}`}
                  className="font-semibold text-white hover:text-[var(--bounty)] transition-colors"
                >
                  {row.reportedUserDisplayName}
                </Link>
              ),
            },
            {
              header: "REASON",
              render: (row) => (
                <div className="text-sm text-[var(--foreground)]/80">
                  <div className="font-semibold mb-0.5">{row.reason}</div>
                  {row.details && (
                    <div className="text-muted leading-relaxed">
                      {expanded.has(row.id) ||
                      row.details.length <= DETAILS_TRUNCATE
                        ? row.details
                        : `${row.details.slice(0, DETAILS_TRUNCATE)}…`}
                      {row.details.length > DETAILS_TRUNCATE && (
                        <button
                          onClick={() => toggleExpand(row.id)}
                          className="ml-1 text-[var(--bounty)] font-semibold underline"
                        >
                          {expanded.has(row.id) ? "less" : "more"}
                        </button>
                      )}
                    </div>
                  )}
                </div>
              ),
            },
            {
              header: "REPORTER",
              render: (row) => (
                <span className="text-sm text-muted">
                  {row.reporterDisplayName}
                </span>
              ),
            },
            {
              header: "FILED",
              align: "right",
              render: (row) => (
                <span className="text-sm text-muted">
                  {timeAgo(row.createdAt)}
                </span>
              ),
            },
            {
              header: "OPEN",
              align: "right",
              render: (row) => (
                <span
                  className="text-sm font-bold"
                  style={{
                    color: countColor(row.openReportsAgainstReportedUser),
                  }}
                  title="Distinct reporters with an open report against this player"
                >
                  {row.openReportsAgainstReportedUser}
                </span>
              ),
            },
            {
              header: "ACTIONS",
              align: "right",
              render: (row) => (
                <div className="flex justify-end gap-2 items-center">
                  {/*
                    A report scoped to a game is the case the per-game engine breakdown exists for:
                    the reporter is pointing at one specific game, so this is where a moderator
                    should be able to look at that game's timings rather than the account's whole
                    history. Expect Insufficient - one game rarely clears the detector's minimums.
                  */}
                  {row.gameId && (
                    <button
                      onClick={() => setGameAnalysis(row.gameId)}
                      title="Engine-assistance timings for this game"
                      className="btn-secondary px-3 py-1.5 text-sm"
                    >
                      Analyse game
                    </button>
                  )}
                  {row.status === "Open" ? (
                    <>
                      <button
                        onClick={() =>
                          setActionModal({ report: row, action: "Reviewed" })
                        }
                        className="btn-secondary px-3 py-1.5 text-sm"
                      >
                        Review
                      </button>
                      <button
                        onClick={() =>
                          setActionModal({ report: row, action: "Dismissed" })
                        }
                        className="btn-danger px-3 py-1.5 text-sm"
                      >
                        Dismiss
                      </button>
                    </>
                  ) : (
                    <span className="text-sm text-muted">{row.status}</span>
                  )}
                </div>
              ),
            },
          ]}
        />
      )}

      {actionModal && (
        <ResolveReportModal
          reportedUserDisplayName={actionModal.report.reportedUserDisplayName}
          action={actionModal.action}
          onCancel={() => setActionModal(null)}
          onConfirm={submitAction}
        />
      )}

      {gameAnalysis && (
        <GameAnalysisModal
          gameId={gameAnalysis}
          onClose={() => setGameAnalysis(null)}
        />
      )}
    </>
  );
}

function ResolveReportModal({
  reportedUserDisplayName,
  action,
  onCancel,
  onConfirm,
}: {
  reportedUserDisplayName: string;
  action: ModerationReportStatus;
  onCancel: () => void;
  onConfirm: (moderatorNote: string) => void;
}) {
  const [note, setNote] = useState("");
  const verb = action === "Reviewed" ? "Mark reviewed" : "Dismiss";

  return (
    <Dialog title={`${verb}: ${reportedUserDisplayName}`} onClose={onCancel}>
      <label className="mt-4 block text-sm text-muted">
        Moderator note (optional)
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value.slice(0, 2000))}
          rows={4}
          className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
        />
      </label>
      <div className="mt-6 flex justify-end gap-3">
        <button
          onClick={onCancel}
          className="btn-secondary px-3 py-1.5 text-sm"
        >
          Cancel
        </button>
        <button
          onClick={() => onConfirm(note)}
          className="btn-primary px-3 py-1.5 text-sm"
        >
          {verb}
        </button>
      </div>
    </Dialog>
  );
}

function SanctionsTab() {
  const [activeOnly, setActiveOnly] = useState(true);
  const [sanctions, setSanctions] = useState<SanctionDto[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [liftTarget, setLiftTarget] = useState<SanctionDto | null>(null);

  function load() {
    let cancelled = false;
    setLoading(true);
    setError(null);
    getSanctions({ activeOnly })
      .then((data) => {
        if (!cancelled) setSanctions(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof ApiError ? err.message : "Failed to load sanctions.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }

  useEffect(load, [activeOnly]);

  async function handleLift(reason: string) {
    if (!liftTarget) return;
    const target = liftTarget;
    setLiftTarget(null);
    try {
      await liftSanction(target.id, { reason });
      load();
    } catch (err) {
      setError(
        err instanceof ApiError ? err.message : "Failed to lift sanction.",
      );
    }
  }

  return (
    <>
      <div className="flex gap-2 mb-6 flex-wrap items-center">
        <TabChip active={activeOnly} onClick={() => setActiveOnly((v) => !v)}>
          Active only
        </TabChip>
      </div>

      {error && <div className="error-banner mb-4">{error}</div>}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={5} />
        </Card>
      ) : !sanctions || sanctions.length === 0 ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No sanctions"
            description="Nothing matches this filter right now."
          />
        </Card>
      ) : (
        <DataTable
          gridTemplateColumns="1.2fr 110px 1.3fr 1fr 100px 100px 90px"
          rows={sanctions}
          rowKey={(row) => row.id}
          rowClassName={(row) => (row.isActive ? "" : "opacity-50")}
          columns={[
            {
              header: "PLAYER",
              render: (row) => (
                <Link
                  href={`/moderation/${row.userId}`}
                  className="font-semibold text-white hover:text-[var(--bounty)] transition-colors"
                >
                  {row.userDisplayName}
                </Link>
              ),
            },
            {
              header: "TYPE",
              render: (row) => (
                <span
                  className="text-sm font-bold"
                  style={{ color: SANCTION_TINT[row.type] }}
                >
                  {row.type}
                </span>
              ),
            },
            {
              header: "REASON",
              render: (row) => (
                <span className="text-sm text-[var(--foreground)]/80">
                  {row.reason}
                </span>
              ),
            },
            {
              header: "ISSUED BY",
              render: (row) => (
                <span className="text-sm text-muted">
                  {row.issuedByDisplayName}
                </span>
              ),
            },
            {
              header: "ISSUED",
              align: "right",
              render: (row) => (
                <span className="text-sm text-muted">
                  {new Date(row.issuedAt).toLocaleDateString()}
                </span>
              ),
            },
            {
              header: "EXPIRES",
              align: "right",
              render: (row) => (
                <span className="text-sm text-muted">
                  {row.expiresAt
                    ? new Date(row.expiresAt).toLocaleDateString()
                    : "Permanent"}
                </span>
              ),
            },
            {
              header: "STATUS",
              align: "right",
              render: (row) =>
                row.isActive ? (
                  <button
                    onClick={() => setLiftTarget(row)}
                    className="btn-secondary px-3 py-1.5 text-sm"
                  >
                    Lift
                  </button>
                ) : (
                  <span className="text-sm text-muted">
                    {row.liftedAt ? "Lifted" : "Expired"}
                  </span>
                ),
            },
          ]}
        />
      )}

      {liftTarget && (
        <LiftSanctionModal
          sanction={liftTarget}
          onCancel={() => setLiftTarget(null)}
          onConfirm={handleLift}
        />
      )}
    </>
  );
}

function LiftSanctionModal({
  sanction,
  onCancel,
  onConfirm,
}: {
  sanction: SanctionDto;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
}) {
  const [reason, setReason] = useState("");

  return (
    <Dialog
      title={`Lift ${sanction.type}: ${sanction.userDisplayName}`}
      onClose={onCancel}
    >
      <label className="mt-4 block text-sm text-muted">
        Reason (required)
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value.slice(0, 500))}
          rows={4}
          className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
        />
      </label>
      <div className="mt-6 flex justify-end gap-3">
        <button
          onClick={onCancel}
          className="btn-secondary px-3 py-1.5 text-sm"
        >
          Cancel
        </button>
        <button
          onClick={() => onConfirm(reason.trim())}
          disabled={reason.trim() === ""}
          className="btn-primary px-3 py-1.5 text-sm"
        >
          Lift sanction
        </button>
      </div>
    </Dialog>
  );
}

/**
 * Per-game engine-assistance breakdown, opened from a report that names a game. Renders one report
 * per player because both sides of a reported game are worth a glance - a report tells you who the
 * reporter suspects, not who was actually doing anything.
 */
function GameAnalysisModal({
  gameId,
  onClose,
}: {
  gameId: string;
  onClose: () => void;
}) {
  const [reports, setReports] = useState<DetectionReportDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notDeployed, setNotDeployed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getEngineGameReport(gameId)
      .then((data) => {
        if (!cancelled) setReports(data);
      })
      .catch((err) => {
        if (cancelled) return;
        if (
          err instanceof ApiError &&
          (err.status === 404 || err.status === 501)
        ) {
          setNotDeployed(true);
        } else {
          setError(
            err instanceof ApiError
              ? err.message
              : "Could not analyse this game.",
          );
        }
      });
    return () => {
      cancelled = true;
    };
  }, [gameId]);

  return (
    <Dialog title="Engine-assistance timings" onClose={onClose} size="xl">
      <p className="text-sm text-muted mt-1 mb-4">
        One game only. `Insufficient` is the normal answer here: a single game
        rarely clears the detector&apos;s minimum ply and game counts, and that
        is correct behaviour, not a failure.
      </p>

      {!reports && !error && !notDeployed && <SkeletonRows count={2} />}
      {error && <p className="text-sm text-[var(--loss)]">{error}</p>}
      {notDeployed && (
        <p className="text-sm text-muted">
          This detector is not deployed in this environment.
        </p>
      )}

      {reports?.map((r) => (
        <div
          key={r.userId}
          className="border-t border-[var(--border)] pt-4 mt-4 first:border-t-0 first:pt-0 first:mt-0"
        >
          <div className="flex items-center gap-3 flex-wrap mb-2">
            <span
              className="inline-flex items-center border px-3 py-1 text-sm font-bold"
              style={{
                color: VERDICT_COLOR[r.verdict],
                borderColor: VERDICT_COLOR[r.verdict],
              }}
            >
              {r.verdict}
            </span>
            <span className="font-bold text-white">{r.displayName}</span>
            <span className="text-sm text-muted">
              {r.sampleSize} scored plies
            </span>
          </div>
          {r.signals.map((s) => (
            <p key={s.name} className="text-sm text-muted mb-1">
              {s.evidence}
            </p>
          ))}
          {r.caveats.map((c, i) => (
            <p
              key={i}
              className="text-sm text-[var(--bounty)]/80 mt-1 flex gap-1.5"
            >
              <IconAlertTriangle
                size={13}
                stroke={2}
                className="shrink-0 mt-0.5"
              />
              {c}
            </p>
          ))}
        </div>
      ))}

      <div className="mt-6 flex justify-end">
        <button onClick={onClose} className="btn-secondary px-3 py-1.5 text-sm">
          Close
        </button>
      </div>
    </Dialog>
  );
}

const SWEEP_WINDOWS = [30, 90, 365];

/**
 * The sandbagging sweep. Every other view in this surface starts from a report somebody filed,
 * which by construction only finds the abusers a player happened to notice and bother reporting;
 * this is the one route that goes looking. Engine assistance is deliberately not a twin (B20a /
 * S-2-3): it stays named-account and named-game lookup, typically after a report. Run on demand
 * behind an explicit button for the same reason the dossier's detector cards are: it scans every
 * recently active player, and a moderator opening a tab should not kick that off by accident.
 */
function SweepTab() {
  const [sinceDays, setSinceDays] = useState(90);
  const [minScore, setMinScore] = useState(0.4);
  const [reports, setReports] = useState<DetectionReportDto[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notDeployed, setNotDeployed] = useState(false);

  function run() {
    setLoading(true);
    setError(null);
    setNotDeployed(false);
    getSandbaggingSweep({ sinceDays, minScore })
      .then((data) => setReports(data))
      .catch((err) => {
        // A 404 means Task B is not deployed in this environment, which is an expected state
        // rather than a failure - same handling as the dossier's detector cards.
        if (
          err instanceof ApiError &&
          (err.status === 404 || err.status === 501)
        ) {
          setNotDeployed(true);
        } else {
          setError(
            err instanceof ApiError ? err.message : "Could not run the sweep.",
          );
        }
      })
      .finally(() => setLoading(false));
  }

  return (
    <div className="flex flex-col gap-5">
      <Card padding="p-5">
        <div className="flex flex-wrap items-center gap-4">
          <div className="flex items-center gap-2">
            <span className="text-sm tracking-widest font-semibold text-muted">
              WINDOW
            </span>
            {SWEEP_WINDOWS.map((d) => (
              <TabChip
                key={d}
                active={sinceDays === d}
                onClick={() => setSinceDays(d)}
              >
                {d}d
              </TabChip>
            ))}
          </div>

          <label className="flex items-center gap-2">
            <span className="text-sm tracking-widest font-semibold text-muted">
              MIN SCORE
            </span>
            <input
              type="number"
              min={0}
              max={1}
              step={0.05}
              value={minScore}
              onChange={(e) =>
                setMinScore(
                  Math.min(1, Math.max(0, Number(e.target.value) || 0)),
                )
              }
              className="w-20 border border-[var(--border)] bg-[var(--background)] px-2 py-1.5 text-sm text-white focus:border-[var(--bounty)] focus:outline-none transition-colors"
            />
          </label>

          <button
            onClick={run}
            disabled={loading}
            className="btn-primary ml-auto px-5 py-2 text-sm"
          >
            {loading ? "Sweeping…" : "Run sweep"}
          </button>
        </div>
        <p className="text-sm text-muted mt-3">
          Advisory. A high score is a reason to open a dossier and read the
          evidence, never a reason to act on its own.
        </p>
      </Card>

      {loading && <SkeletonRows count={4} />}
      {error && <p className="text-sm text-[var(--loss)]">{error}</p>}

      {notDeployed && (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="This detector is not deployed yet"
            description="The sandbagging detector is not available in this environment."
          />
        </Card>
      )}

      {reports !== null && !loading && !notDeployed && reports.length === 0 && (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="Nobody above that score"
            description="No player in this window scored at or above the threshold. That is not proof of a clean pool - the detector has never been validated against a confirmed sandbagger."
          />
        </Card>
      )}

      {reports !== null &&
        !loading &&
        reports.map((r) => (
          <Link
            key={`${r.userId}-${r.pool ?? "all"}`}
            href={`/moderation/${r.userId}`}
          >
            <Card
              padding="p-4"
              className="hover:border-[var(--bounty)] transition-colors"
            >
              <div className="flex items-center gap-4 flex-wrap">
                <span
                  className="inline-flex items-center border px-3 py-1 text-sm font-bold tracking-wide shrink-0"
                  style={{
                    color: VERDICT_COLOR[r.verdict],
                    borderColor: VERDICT_COLOR[r.verdict],
                  }}
                >
                  {r.verdict}
                </span>
                <span className="font-bold text-white">{r.displayName}</span>
                {r.pool && <span className="text-sm text-muted">{r.pool}</span>}
                <span className="text-sm text-muted">{r.sampleSize} games</span>
                <span
                  className="ml-auto text-lg font-bold"
                  style={{ color: VERDICT_COLOR[r.verdict] }}
                >
                  {Math.round(r.score * 100)}%
                </span>
              </div>
              {r.signals.length > 0 && (
                <p className="text-sm text-muted mt-2">
                  {
                    [...r.signals].sort(
                      (a, b) => b.contribution - a.contribution,
                    )[0].evidence
                  }
                </p>
              )}
            </Card>
          </Link>
        ))}
    </div>
  );
}

/**
 * The Phase 8a.4 moderator display-name scan. `problemsOnly` defaults to `true`, matching the
 * server default: with it on, an empty result is the *good* outcome (every name passes policy
 * and keys consistently), not an absence of data, so the empty state is worded that way.
 *
 * Never renders `storedNormalized`/`recomputedNormalized` raw, only the derived `normalizedDrift`
 * marker, and never echoes blocklist contents; `rejection` is the stable machine name the server
 * already scrubbed of any blocklist term (README scope decision 2).
 */
function NamesTab() {
  const [problemsOnly, setProblemsOnly] = useState(true);
  const [rows, setRows] = useState<DisplayNameScanRowDto[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [renameTarget, setRenameTarget] =
    useState<DisplayNameScanRowDto | null>(null);

  function load() {
    let cancelled = false;
    setLoading(true);
    setError(null);
    getDisplayNameScan({ problemsOnly })
      .then((data) => {
        if (!cancelled) setRows(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof ApiError
            ? err.message
            : "Failed to load display names.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }

  useEffect(load, [problemsOnly]);

  async function handleRename(newName: string, reason: string) {
    if (!renameTarget) return;
    const target = renameTarget;
    setRenameTarget(null);
    try {
      await moderatorRenameDisplayName(target.userId, {
        displayName: newName,
        reason,
      });
      load();
    } catch (err) {
      setError(
        err instanceof ApiError ? err.message : "Failed to rename that player.",
      );
    }
  }

  return (
    <>
      <div className="flex gap-2 mb-4 flex-wrap items-center">
        <TabChip
          active={problemsOnly}
          onClick={() => setProblemsOnly((v) => !v)}
        >
          Problems only
        </TabChip>
      </div>

      <p className="text-sm text-muted mb-6 max-w-2xl leading-relaxed">
        <span className="font-semibold" style={{ color: "var(--bounty)" }}>
          Drift
        </span>{" "}
        means the migration&apos;s one-time backfill keyed this row with its
        simpler rule (lowercase + strip non-alphanumeric only); the
        server&apos;s real normalizer disagrees with what is stored. The row is
        not necessarily wrong today, but a rename is needed to resync its key
        with what a new signup would actually collide against.
      </p>

      {error && <div className="error-banner mb-4">{error}</div>}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={5} />
        </Card>
      ) : !rows || rows.length === 0 ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title={
              problemsOnly
                ? "No display names need attention"
                : "No display names"
            }
            description={
              problemsOnly
                ? "Every display name currently passes policy and keys consistently. Nothing to do."
                : "There are no accounts to show."
            }
          />
        </Card>
      ) : (
        <DataTable
          gridTemplateColumns="1.3fr 1fr 90px 1.3fr 90px"
          rows={rows}
          rowKey={(row) => row.userId}
          columns={[
            {
              header: "DISPLAY NAME",
              render: (row) => (
                <Link
                  href={`/moderation/${row.userId}`}
                  className="font-semibold text-white hover:text-[var(--bounty)] transition-colors"
                >
                  {row.displayName}
                </Link>
              ),
            },
            {
              header: "WHY IT FAILS",
              render: (row) => (
                <span
                  className={`text-sm ${
                    row.rejection
                      ? "text-[var(--loss)] font-semibold"
                      : "text-muted"
                  }`}
                >
                  {row.rejection ?? "-"}
                </span>
              ),
            },
            {
              header: "DRIFT",
              render: (row) =>
                row.normalizedDrift ? (
                  <span
                    title="Keyed by the migration's simpler rule; recompute disagrees."
                    className="inline-flex items-center gap-1 text-sm font-bold"
                    style={{ color: "var(--bounty)" }}
                  >
                    <IconAlertTriangle size={13} stroke={2} />
                    drift
                  </span>
                ) : (
                  <span className="text-sm text-muted">-</span>
                ),
            },
            {
              header: "COLLISIONS",
              render: (row) =>
                row.collidesWithDisplayNames.length > 0 ? (
                  <span
                    className="text-sm"
                    style={{ color: "var(--loss)" }}
                    title="Other accounts sharing this row's recomputed key"
                  >
                    {row.collidesWithDisplayNames.join(", ")}
                  </span>
                ) : (
                  <span className="text-sm text-muted">-</span>
                ),
            },
            {
              header: "ACTIONS",
              align: "right",
              render: (row) => (
                <button
                  onClick={() => setRenameTarget(row)}
                  className="btn-secondary inline-flex gap-1.5 px-3 py-1.5 text-sm"
                >
                  <IconPencil size={13} stroke={2} />
                  Rename
                </button>
              ),
            },
          ]}
        />
      )}

      {renameTarget && (
        <RenameDisplayNameModal
          row={renameTarget}
          onCancel={() => setRenameTarget(null)}
          onConfirm={handleRename}
        />
      )}
    </>
  );
}

function RenameDisplayNameModal({
  row,
  onCancel,
  onConfirm,
}: {
  row: DisplayNameScanRowDto;
  onCancel: () => void;
  onConfirm: (displayName: string, reason: string) => void;
}) {
  const [name, setName] = useState(row.displayName);
  const [reason, setReason] = useState("");
  const canSubmit = name.trim() !== "" && reason.trim() !== "";

  return (
    <Dialog title={`Rename: ${row.displayName}`} onClose={onCancel}>
      <p className="mt-1 text-sm text-muted">
        A moderator rename bypasses and resets the player&apos;s cooldown.
      </p>
      <label className="mt-4 block text-sm text-muted">
        New display name
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
        />
      </label>
      <label className="mt-4 block text-sm text-muted">
        Reason (required: recorded on the audit trail, never shown to the
        player)
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value.slice(0, 500))}
          rows={3}
          className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
        />
      </label>
      <div className="mt-6 flex justify-end gap-3">
        <button
          onClick={onCancel}
          className="btn-secondary px-3 py-1.5 text-sm"
        >
          Cancel
        </button>
        <button
          onClick={() => onConfirm(name.trim(), reason.trim())}
          disabled={!canSubmit}
          className="btn-primary px-3 py-1.5 text-sm"
        >
          Rename
        </button>
      </div>
    </Dialog>
  );
}

const RETENTION_WINDOWS: { days: number; label: string }[] = [
  { days: 90, label: "3 months" },
  { days: 180, label: "6 months" },
  { days: 365, label: "1 year" },
  { days: 730, label: "2 years" },
];

/**
 * The Phase 8a.3 dormant-account report. A read-only work queue: "which accounts have fallen out
 * of retention policy at N days, and why", never an action. See README scope decision 2: dormant
 * accounts are reported, never reaped, because there is no verified mail path yet and erasure is
 * irreversible. There is deliberately no button here that would need a `POST`.
 */
function RetentionTab() {
  const [inactiveDays, setInactiveDays] = useState(365);
  const [skip, setSkip] = useState(0);
  const [data, setData] = useState<DormantAccountScanDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    getDormantAccounts({ inactiveDays, skip })
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((err) => {
        if (cancelled) return;
        setError(
          err instanceof ApiError
            ? err.message
            : "Failed to load dormant accounts.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [inactiveDays, skip]);

  function selectWindow(days: number) {
    setInactiveDays(days);
    setSkip(0);
  }

  const rows = data?.rows ?? [];
  const hasPrev = skip > 0;
  const hasNext = data !== null && skip + rows.length < data.dormantCount;

  return (
    <>
      <div className="flex gap-2 mb-6 flex-wrap items-center">
        <span className="text-sm tracking-widest font-semibold text-muted mr-1">
          WINDOW
        </span>
        {RETENTION_WINDOWS.map((w) => (
          <TabChip
            key={w.days}
            active={inactiveDays === w.days}
            onClick={() => selectWindow(w.days)}
          >
            {w.label}
          </TabChip>
        ))}
      </div>

      {error && <div className="error-banner mb-4">{error}</div>}

      {data && (
        <Card padding="p-4" className="mb-6">
          <p className="text-sm text-[var(--foreground)]">
            <span className="font-bold text-white">{data.dormantCount}</span> of{" "}
            <span className="font-semibold">{data.totalAccounts}</span> accounts
            fall outside a{" "}
            {RETENTION_WINDOWS.find((w) => w.days === data.inactiveDays)
              ?.label ?? `${data.inactiveDays}-day`}{" "}
            window.
          </p>
          {data.truncated && (
            <p
              className="text-sm font-semibold mt-2"
              style={{ color: "var(--bounty)" }}
            >
              This scan hit its cap: the count above is a floor, not a total.
            </p>
          )}
          <p className="text-sm text-muted mt-2">
            Nothing on this screen deletes anything. No account is erased
            automatically: a moderator would have to act by hand, elsewhere.
          </p>
        </Card>
      )}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={5} />
        </Card>
      ) : rows.length === 0 ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No accounts fell out of policy"
            description="Nothing matches this window right now: that is good news."
          />
        </Card>
      ) : (
        <>
          <DataTable
            gridTemplateColumns="1.3fr 130px 110px 80px 110px 160px"
            rows={rows}
            rowKey={(row) => row.userId}
            columns={[
              {
                header: "PLAYER",
                render: (row: DormantAccountRowDto) => (
                  <Link
                    href={`/moderation/${row.userId}`}
                    className="font-semibold text-white hover:text-[var(--bounty)] transition-colors"
                  >
                    {row.displayName}
                  </Link>
                ),
              },
              {
                header: "REASON",
                render: (row: DormantAccountRowDto) => (
                  <span
                    className="text-sm font-bold"
                    style={{ color: DORMANCY_TINT[row.reason] }}
                  >
                    {row.reason}
                  </span>
                ),
              },
              {
                header: "SIGNED UP",
                align: "right",
                render: (row: DormantAccountRowDto) => (
                  <span className="text-sm text-muted">
                    {timeAgo(row.createdAt)}
                  </span>
                ),
              },
              {
                header: "GAMES",
                align: "right",
                render: (row: DormantAccountRowDto) => (
                  <span className="text-sm text-[var(--foreground)]/80">
                    {row.gamesPlayed}
                  </span>
                ),
              },
              {
                header: "LAST GAME",
                align: "right",
                render: (row: DormantAccountRowDto) => (
                  <span className="text-sm text-muted">
                    {row.lastGameAt ? timeAgo(row.lastGameAt) : "-"}
                  </span>
                ),
              },
              {
                header: "FLAGS",
                render: (row: DormantAccountRowDto) => (
                  <div className="flex gap-1.5 flex-wrap">
                    {!row.emailConfirmed && (
                      <span className="inline-flex items-center border border-[var(--border)] px-2 py-0.5 text-sm font-semibold text-muted">
                        unconfirmed
                      </span>
                    )}
                    {row.hasSanctions && (
                      <span
                        className="inline-flex items-center border px-2 py-0.5 text-sm font-semibold"
                        style={{
                          color: "var(--loss)",
                          borderColor: "var(--loss)",
                        }}
                      >
                        sanctioned
                      </span>
                    )}
                  </div>
                ),
              },
            ]}
          />

          <div className="flex items-center justify-between mt-4">
            <span className="text-sm text-muted">
              Showing {skip + 1}–{skip + rows.length} of{" "}
              {data?.dormantCount ?? 0}
            </span>
            <div className="flex gap-2">
              <button
                onClick={() => setSkip((s) => Math.max(0, s - rows.length))}
                disabled={!hasPrev}
                className="btn-secondary px-3 py-1.5 text-sm"
              >
                Previous
              </button>
              <button
                onClick={() => setSkip((s) => s + rows.length)}
                disabled={!hasNext}
                className="btn-secondary px-3 py-1.5 text-sm"
              >
                Next
              </button>
            </div>
          </div>
        </>
      )}
    </>
  );
}

function LookupTab() {
  const router = useRouter();
  const [userId, setUserId] = useState("");

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const trimmed = userId.trim();
    if (!trimmed) return;
    router.push(`/moderation/${trimmed}`);
  }

  return (
    <Card padding="p-8" className="max-w-md">
      <p className="text-sm text-muted mb-4">
        There is no player search yet: a moderator normally arrives here from a
        report row. This is the manual fallback: paste a user id to open their
        dossier directly.
      </p>
      <form onSubmit={handleSubmit} className="flex flex-col gap-3">
        <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
          User id
          <input
            type="text"
            value={userId}
            onChange={(e) => setUserId(e.target.value)}
            placeholder="00000000-0000-0000-0000-000000000000"
            className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none"
          />
        </label>
        <button
          type="submit"
          className="btn-primary mt-2 gap-2 px-5 py-2 text-sm"
        >
          <IconSearch size={16} stroke={2} />
          Open dossier
        </button>
      </form>
    </Card>
  );
}

export default function ModerationPage() {
  const { isAdmin, hydrated } = useAuth();
  const [tab, setTab] = useState<Tab>("reports");

  // Phase 9d.2. `isAdmin` (the role claim) no longer implies the moderation surface actually
  // answers: every route behind `ModerationPolicies.Admin` also requires the `mfa` claim, which
  // only exists once the account has enrolled (README § 2). `null` = not checked yet, `true`/`false`
  // = the answer. A fetch failure is treated as "unknown" and falls through to the dashboard rather
  // than blocking it: the server enforces the real gate on every route regardless, so this check is
  // purely explanatory, not a security boundary, and must not itself become a false negative.
  const [twoFactorEnabled, setTwoFactorEnabled] = useState<boolean | null>(
    null,
  );

  useEffect(() => {
    if (!hydrated || !isAdmin) return;
    let cancelled = false;
    getTwoFactorStatus()
      .then((res) => {
        if (!cancelled) setTwoFactorEnabled(res.enabled);
      })
      .catch(() => {
        if (!cancelled) setTwoFactorEnabled(true);
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, isAdmin]);

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      {/* isAdmin only hides page content; the server enforces this on every route regardless. */}
      <StaffOnly>
        <RequireAuth>
          <main
            id="main-content"
            className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
          >
            <h1 className="text-3xl font-bold leading-tight tracking-tight mb-8 text-white text-balance">
              Moderation
            </h1>

            {twoFactorEnabled === false ? (
              /*
              Phase 9d.2. This explains a 403 the user would otherwise experience as the dashboard
              mysteriously failing: every request below would 403 anyway, this just says why up
              front instead of letting each tab discover it independently.
            */
              <Card padding="py-12 px-8" className="shadow-lg">
                <EmptyState
                  title="Two-factor authentication is required for moderators"
                  description="Your moderator access is inactive until you enable it. Set it up on your profile. It takes a minute."
                >
                  <Link
                    href="/profile"
                    className="btn-primary gap-1.5 px-6 py-3 text-sm"
                  >
                    Go to profile →
                  </Link>
                </EmptyState>
              </Card>
            ) : (
              <>
                <div className="flex gap-2 mb-8 flex-wrap">
                  <TabButton
                    active={tab === "reports"}
                    onClick={() => setTab("reports")}
                    icon={<IconFlag size={16} stroke={2} />}
                    label="Reports"
                  />
                  <TabButton
                    active={tab === "sanctions"}
                    onClick={() => setTab("sanctions")}
                    icon={<IconGavel size={16} stroke={2} />}
                    label="Sanctions"
                  />
                  <TabButton
                    active={tab === "sweep"}
                    onClick={() => setTab("sweep")}
                    icon={<IconChartLine size={16} stroke={2} />}
                    label="Sandbagging sweep"
                  />
                  <TabButton
                    active={tab === "names"}
                    onClick={() => setTab("names")}
                    icon={<IconUser size={16} stroke={2} />}
                    label="Display names"
                  />
                  <TabButton
                    active={tab === "lookup"}
                    onClick={() => setTab("lookup")}
                    icon={<IconSearch size={16} stroke={2} />}
                    label="Look up a player"
                  />
                  <TabButton
                    active={tab === "retention"}
                    onClick={() => setTab("retention")}
                    icon={<IconClockOff size={16} stroke={2} />}
                    label="Retention"
                  />
                </div>

                {tab === "reports" && (
                  <section>
                    <h2 className="sr-only">Reports</h2>
                    <ReportsTab />
                  </section>
                )}
                {tab === "sanctions" && (
                  <section>
                    <h2 className="sr-only">Sanctions</h2>
                    <SanctionsTab />
                  </section>
                )}
                {tab === "sweep" && (
                  <section>
                    <h2 className="sr-only">Sandbagging sweep</h2>
                    <SweepTab />
                  </section>
                )}
                {tab === "names" && (
                  <section>
                    <h2 className="sr-only">Display names</h2>
                    <NamesTab />
                  </section>
                )}
                {tab === "lookup" && (
                  <section>
                    <h2 className="sr-only">Look up a player</h2>
                    <LookupTab />
                  </section>
                )}
                {tab === "retention" && (
                  <section>
                    <h2 className="sr-only">Retention</h2>
                    <RetentionTab />
                  </section>
                )}
              </>
            )}
          </main>
        </RequireAuth>
      </StaffOnly>
      <Footer />
    </div>
  );
}
