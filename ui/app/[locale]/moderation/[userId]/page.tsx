"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Link } from "@/i18n/navigation";
import { useParams } from "next/navigation";
import { IconArrowLeft, IconGavel } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { DataTable } from "@/components/DataTable";
import { DetectionReportCard } from "@/components/DetectionReportCard";
import { Dialog } from "@/components/Dialog";
import { EmptyState } from "@/components/EmptyState";
import { TabChip } from "@/components/TabChip";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { useAuth } from "@/components/AuthProvider";
import { RequireAuth } from "@/components/RequireAuth";
import { StaffOnly } from "@/components/ForbiddenScreen";
import { SkeletonRows } from "@/components/SkeletonRows";
import {
  ApiError,
  createSanction,
  eraseModerationAccount,
  exportModerationAccount,
  getEngineReport,
  getModerationUser,
  getSandbaggingReport,
  liftSanction,
} from "@/lib/api";
import type {
  ModerationRatingDto,
  ModerationUserDto,
  SanctionDto,
  SanctionTypeName,
} from "@/lib/types";

const SANCTION_TINT: Record<SanctionTypeName, string> = {
  Warning: "var(--bounty)",
  RatedPlayBan: "var(--accent)",
  AccountBan: "var(--loss)",
};

const SANCTION_TYPES: {
  value: SanctionTypeName;
  label: string;
  effect: string;
}[] = [
  {
    value: "Warning",
    label: "Warning",
    effect:
      "Recorded and shown to the player. Changes nothing about what they can do.",
  },
  {
    value: "RatedPlayBan",
    label: "Rated play ban",
    effect:
      "Blocked from rated matchmaking. Unrated and custom games still work.",
  },
  {
    value: "AccountBan",
    label: "Account ban",
    effect: "Cannot log in, refresh a token, queue, or create a game.",
  },
];

/** Expiry presets. `null` days means permanent - the server stores that as a null ExpiresAt. */
const DURATIONS: { label: string; days: number | null }[] = [
  { label: "Permanent", days: null },
  { label: "1 day", days: 1 },
  { label: "7 days", days: 7 },
  { label: "30 days", days: 30 },
];

const REASON_MAX = 500;
const NOTE_MAX = 2000;

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

function expiryLabel(sanction: SanctionDto): string {
  if (sanction.liftedAt) return `Lifted ${formatDate(sanction.liftedAt)}`;
  if (!sanction.expiresAt) return "Permanent";
  const remaining = new Date(sanction.expiresAt).getTime() - Date.now();
  if (remaining <= 0) return `Expired ${formatDate(sanction.expiresAt)}`;
  const days = Math.ceil(remaining / 86_400_000);
  return `${days} day${days === 1 ? "" : "s"} left`;
}

function StatCard({ label, value }: { label: string; value: number }) {
  return (
    <Card padding="p-4">
      <div className="text-sm tracking-widest text-muted font-semibold">
        {label.toUpperCase()}
      </div>
      <div className="text-2xl font-bold text-white mt-1">{value}</div>
    </Card>
  );
}

function RatingRow({ rating }: { rating: ModerationRatingDto }) {
  return (
    <Card padding="p-4">
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm tracking-widest text-muted font-semibold">
          {rating.pool.toUpperCase()}
        </span>
        {rating.isProvisional && (
          <span className="text-sm tracking-wide text-muted font-semibold">
            PROVISIONAL
          </span>
        )}
      </div>
      <div className="text-2xl font-bold text-white mt-1">
        {Math.round(rating.value)}
      </div>
      <div className="text-sm text-muted mt-1">
        RD {Math.round(rating.ratingDeviation)} - {rating.gamesPlayed} games
      </div>
    </Card>
  );
}

/**
 * Issue-a-sanction form. The two text fields are labelled with who reads them, right in the UI:
 * the server keeps `moderatorNote` off every player-facing DTO by construction, and the person
 * typing needs to know which box is which at the moment they type it.
 */
function IssueSanctionForm({
  userId,
  onIssued,
}: {
  userId: string;
  onIssued: () => void;
}) {
  const [type, setType] = useState<SanctionTypeName>("Warning");
  const [reason, setReason] = useState("");
  const [note, setNote] = useState("");
  const [durationDays, setDurationDays] = useState<number | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const selected = SANCTION_TYPES.find((t) => t.value === type);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!reason.trim() || submitting) return;

    setSubmitting(true);
    setError(null);
    try {
      const expiresAt =
        durationDays === null
          ? null
          : new Date(Date.now() + durationDays * 86_400_000).toISOString();
      await createSanction({
        userId,
        type,
        reason: reason.trim(),
        moderatorNote: note.trim() || null,
        expiresAt,
        reportId: null,
      });
      setReason("");
      setNote("");
      onIssued();
    } catch (err) {
      setError(
        err instanceof ApiError ? err.message : "Could not issue the sanction.",
      );
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <div className="flex flex-wrap gap-2">
        {SANCTION_TYPES.map((t) => (
          <button
            key={t.value}
            type="button"
            onClick={() => setType(t.value)}
            className={`px-4 py-2 text-sm font-semibold border transition-colors ${
              type === t.value
                ? "text-[var(--background)]"
                : "bg-[var(--surface)] text-[var(--foreground)] border-[var(--border)] hover:border-[var(--foreground)]/40"
            }`}
            style={
              type === t.value
                ? {
                    backgroundColor: SANCTION_TINT[t.value],
                    borderColor: SANCTION_TINT[t.value],
                  }
                : undefined
            }
          >
            {t.label}
          </button>
        ))}
      </div>
      {selected && (
        <p className="text-sm text-muted -mt-2">{selected.effect}</p>
      )}

      <label className="flex flex-col gap-1.5">
        <span className="text-sm tracking-widest font-semibold text-muted">
          REASON: SHOWN TO THE PLAYER
        </span>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value.slice(0, REASON_MAX))}
          rows={2}
          placeholder="Write it as if they will read it, because they will. Do not name or sign yourself."
          className="bg-[var(--background)] border border-[var(--border)] px-3 py-2 text-sm text-white focus:outline-none focus:border-[var(--bounty)] transition-colors"
        />
        <span className="text-sm text-muted self-end">
          {reason.length}/{REASON_MAX}
        </span>
      </label>

      <label className="flex flex-col gap-1.5">
        <span className="text-sm tracking-widest font-semibold text-muted">
          NOTE: MODERATOR ONLY
        </span>
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value.slice(0, NOTE_MAX))}
          rows={2}
          placeholder="Context for whoever reviews this later. Never shown to the player."
          className="bg-[var(--background)] border border-[var(--border)] px-3 py-2 text-sm text-white focus:outline-none focus:border-[var(--bounty)] transition-colors"
        />
        <span className="text-sm text-muted self-end">
          {note.length}/{NOTE_MAX}
        </span>
      </label>

      <div className="flex flex-wrap gap-2 items-center">
        <span className="text-sm tracking-widest font-semibold text-muted">
          DURATION
        </span>
        {DURATIONS.map((d) => (
          <TabChip
            key={d.label}
            active={durationDays === d.days}
            onClick={() => setDurationDays(d.days)}
          >
            {d.label}
          </TabChip>
        ))}
      </div>

      {error && <p className="text-sm text-[var(--loss)]">{error}</p>}

      <button
        type="submit"
        disabled={!reason.trim() || submitting}
        className="btn-danger-fill self-start px-5 py-2 text-sm"
      >
        {submitting ? "Issuing…" : "Issue sanction"}
      </button>
    </form>
  );
}

function LiftDialog({
  sanction,
  onClose,
  onLifted,
}: {
  sanction: SanctionDto;
  onClose: () => void;
  onLifted: () => void;
}) {
  const [reason, setReason] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!reason.trim() || submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      await liftSanction(sanction.id, { reason: reason.trim() });
      onLifted();
    } catch (err) {
      setError(
        err instanceof ApiError ? err.message : "Could not lift the sanction.",
      );
      setSubmitting(false);
    }
  }

  return (
    <Dialog title="Lift this sanction" onClose={onClose} size="lg">
      <p className="text-sm text-muted mb-4">
        The row is kept either way: lifting records who lifted it and why, and
        that history is the audit record of this action, not an appeal path -
        there is no separate appeal process on this site.
      </p>
      <form onSubmit={submit} className="flex flex-col gap-3">
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value.slice(0, REASON_MAX))}
          rows={3}
          autoFocus
          placeholder="Why is this being lifted? Do not name or sign yourself."
          className="bg-[var(--background)] border border-[var(--border)] px-3 py-2 text-sm text-white focus:outline-none focus:border-[var(--bounty)] transition-colors"
        />
        {error && <p className="text-sm text-[var(--loss)]">{error}</p>}
        <div className="flex gap-2 justify-end">
          <button
            type="button"
            onClick={onClose}
            className="btn-secondary px-3 py-1.5 text-sm"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={!reason.trim() || submitting}
            className="btn-primary px-3 py-1.5 text-sm"
          >
            {submitting ? "Lifting…" : "Lift"}
          </button>
        </div>
      </form>
    </Dialog>
  );
}

const ERASE_ACKNOWLEDGEMENT = "ERASE THIS ACCOUNT";

function GdprSubjectActions({
  userId,
  isSelf,
  alreadyDeleted,
  onErased,
}: {
  userId: string;
  isSelf: boolean;
  alreadyDeleted: boolean;
  onErased: () => void;
}) {
  const [exporting, setExporting] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [acknowledgement, setAcknowledgement] = useState("");
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);

  async function onExport() {
    setStatus(null);
    setExporting(true);
    try {
      const { blob, filename } = await exportModerationAccount(userId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = filename;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setStatus(
        err instanceof ApiError
          ? err.message
          : "Could not export this account.",
      );
    } finally {
      setExporting(false);
    }
  }

  async function onErase() {
    if (acknowledgement !== ERASE_ACKNOWLEDGEMENT || busy) return;
    setBusy(true);
    setStatus(null);
    try {
      await eraseModerationAccount(userId, acknowledgement);
      setConfirming(false);
      setAcknowledgement("");
      onErased();
    } catch (err) {
      setStatus(
        err instanceof ApiError ? err.message : "Could not erase this account.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card padding="p-5">
      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={onExport}
          disabled={exporting || alreadyDeleted}
          className="btn-secondary px-5 py-2 text-sm"
        >
          {exporting ? "Exporting…" : "Export data"}
        </button>
        <button
          type="button"
          onClick={() => {
            setConfirming(true);
            setStatus(null);
          }}
          disabled={isSelf || alreadyDeleted}
          className="btn-secondary px-5 py-2 text-sm"
        >
          Erase account
        </button>
      </div>
      {isSelf && (
        <p className="text-sm text-muted mt-3 m-0">
          You cannot erase your own account on this route.
        </p>
      )}
      {alreadyDeleted && (
        <p className="text-sm text-muted mt-3 m-0">
          This account is already a tombstone. Export and erasure are closed.
        </p>
      )}
      {status && (
        <p className="text-sm text-[var(--loss)] mt-3 m-0">{status}</p>
      )}
      {confirming && (
        <Dialog
          title="Erase this account"
          onClose={() => {
            if (!busy) {
              setConfirming(false);
              setAcknowledgement("");
            }
          }}
        >
          <p className="mt-4 text-sm text-[var(--foreground)]">
            Irreversible anonymisation. Games stay on the site attributed to
            &ldquo;Deleted account&rdquo;. Sanctions remain. Type the phrase
            exactly.
          </p>
          <label className="mt-4 block text-sm text-muted">
            Type {ERASE_ACKNOWLEDGEMENT}
            <input
              value={acknowledgement}
              onChange={(e) => setAcknowledgement(e.target.value)}
              autoComplete="off"
              className="mt-1 w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-[var(--foreground)]"
            />
          </label>
          {status && (
            <p className="mt-2 text-sm text-[var(--loss)]">{status}</p>
          )}
          <div className="mt-6 flex justify-end gap-3">
            <button
              type="button"
              onClick={() => {
                setConfirming(false);
                setAcknowledgement("");
              }}
              disabled={busy}
              className="btn-secondary px-3 py-1.5 text-sm"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={onErase}
              disabled={acknowledgement !== ERASE_ACKNOWLEDGEMENT || busy}
              className="btn-primary px-3 py-1.5 text-sm"
            >
              {busy ? "Erasing…" : "Permanently erase"}
            </button>
          </div>
        </Dialog>
      )}
    </Card>
  );
}

function Dossier({ userId }: { userId: string }) {
  const { userId: myId } = useAuth();
  const [user, setUser] = useState<ModerationUserDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lifting, setLifting] = useState<SanctionDto | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    getModerationUser(userId)
      .then((data) => {
        setUser(data);
        setError(null);
      })
      .catch((err) =>
        setError(
          err instanceof ApiError && err.status === 404
            ? "No such player."
            : err instanceof ApiError
              ? err.message
              : "Could not load this player.",
        ),
      )
      .finally(() => setLoading(false));
  }, [userId]);

  useEffect(load, [load]);

  if (loading)
    return (
      <Card padding="py-12 px-8" className="shadow-lg">
        {/* Staff pages are hardcoded English by documented decision (frontend.md); no i18n key
            for this loading state exists, so this stays a plain string rather than inventing one. */}
        <h1 className="sr-only">Player dossier</h1>
        <SkeletonRows count={6} />
      </Card>
    );

  if (error || !user) {
    return (
      <Card padding="py-12 px-8" className="shadow-lg">
        <EmptyState
          headingLevel={1}
          title="Player not found"
          description={error ?? "Could not load this player."}
        />
      </Card>
    );
  }

  const activeSanctions = user.sanctions.filter((s) => s.isActive);

  return (
    <div className="flex flex-col gap-8">
      {/* HEADER */}
      <header className="flex flex-col gap-3 pb-6 border-b border-[var(--border)]">
        <div className="flex items-center gap-3 flex-wrap">
          <h1 className="text-3xl font-bold leading-tight tracking-tight text-white text-balance m-0">
            {user.displayName}
          </h1>
          {!user.emailConfirmed && (
            <span className="text-sm tracking-widest font-semibold text-[var(--loss)] border border-[var(--loss)] px-2.5 py-1">
              EMAIL UNCONFIRMED
            </span>
          )}
        </div>
        <div className="flex items-center gap-3 text-sm text-muted flex-wrap">
          <span>{user.email}</span>
          <span aria-hidden="true" className="text-muted">
            -
          </span>
          <span>Joined {formatDate(user.createdAt)}</span>
          <span aria-hidden="true" className="text-muted">
            -
          </span>
          <span className="font-mono text-sm">{user.userId}</span>
        </div>
      </header>

      <section>
        <h2 className="text-lg font-bold text-white mb-1">
          Data-subject request
        </h2>
        <p className="text-sm text-muted mb-3">
          Access, portability and erasure for a banned account, or an under-16
          close on credible notice. Same operations as the signed-in profile
          routes; sanctions still survive. Type{" "}
          <code className="font-mono text-[var(--bounty)]">
            ERASE THIS ACCOUNT
          </code>{" "}
          to confirm erasure. There is no undo.
        </p>
        <GdprSubjectActions
          userId={user.userId}
          isSelf={myId === user.userId}
          alreadyDeleted={user.displayName === "Deleted account" && !user.email}
          onErased={load}
        />
      </section>

      {activeSanctions.map((s) => (
        <Card key={s.id} accentColorVar="--loss" padding="p-5">
          <div className="flex items-center gap-2 mb-1">
            <IconGavel size={18} stroke={1.75} color="var(--loss)" />
            <span className="font-bold text-white">
              Active{" "}
              {s.type === "RatedPlayBan"
                ? "rated play ban"
                : s.type.toLowerCase()}
            </span>
            <span className="text-sm text-muted">- {expiryLabel(s)}</span>
          </div>
          <p className="text-sm text-[var(--foreground)]/80">{s.reason}</p>
        </Card>
      ))}

      {/* COUNTS */}
      <section>
        <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-3">
          <StatCard label="Games played" value={user.gamesPlayed} />
          <StatCard label="Games abandoned" value={user.gamesAbandoned} />
          <StatCard label="Grace disconnects" value={user.graceDisconnects} />
          <StatCard label="Reports against" value={user.reportsAgainst} />
          <StatCard label="Reports filed" value={user.reportsFiled} />
        </div>
        <p className="text-sm text-muted mt-3">
          Grace disconnects count times this player dropped with no connection
          left, including returns inside the 30-second window. They are not a
          sanction. Games abandoned counts only games that ended as a forfeit.
        </p>
      </section>

      {/* RATINGS */}
      <section>
        <h2 className="text-lg font-bold text-white mb-3">Ratings</h2>
        {user.ratings.length === 0 ? (
          <p className="text-sm text-muted">No rated games in any pool.</p>
        ) : (
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
            {user.ratings.map((r) => (
              <RatingRow key={r.pool} rating={r} />
            ))}
          </div>
        )}
      </section>

      {/* SANCTION HISTORY */}
      <section>
        <h2 className="text-lg font-bold text-white mb-1">Sanction history</h2>
        <p className="text-sm text-muted mb-3">
          Every sanction ever issued, lifted and expired included. This is the
          audit record, not an appeal path: it is never filtered.
        </p>
        {user.sanctions.length === 0 ? (
          <p className="text-sm text-muted">
            This account has never been sanctioned.
          </p>
        ) : (
          <DataTable
            columns={[
              {
                header: "TYPE",
                render: (s: SanctionDto) => (
                  <span
                    className="font-semibold text-sm"
                    style={{ color: SANCTION_TINT[s.type] }}
                  >
                    {s.type}
                  </span>
                ),
              },
              {
                header: "REASON",
                render: (s: SanctionDto) => (
                  <span className="text-sm">{s.reason}</span>
                ),
              },
              {
                header: "NOTE",
                render: (s: SanctionDto) => (
                  <span className="text-sm text-muted">
                    {s.moderatorNote ?? "-"}
                  </span>
                ),
              },
              {
                header: "ISSUED BY",
                render: (s: SanctionDto) => (
                  <span className="text-sm">{s.issuedByDisplayName}</span>
                ),
              },
              {
                header: "ISSUED",
                align: "right",
                render: (s: SanctionDto) => (
                  <span className="text-sm">{formatDate(s.issuedAt)}</span>
                ),
              },
              {
                header: "STATUS",
                align: "right",
                render: (s: SanctionDto) => (
                  <div className="flex items-center gap-2 justify-end">
                    <span className="text-sm text-muted">{expiryLabel(s)}</span>
                    {s.isActive && (
                      <button
                        onClick={() => setLifting(s)}
                        className="btn-secondary px-3 py-1.5 text-sm"
                      >
                        Lift
                      </button>
                    )}
                  </div>
                ),
              },
            ]}
            rows={user.sanctions}
            gridTemplateColumns="120px 1.4fr 1.2fr 120px 100px 180px"
            rowKey={(s) => s.id}
            rowClassName={(s) => (s.isActive ? "" : "opacity-50")}
          />
        )}
      </section>

      {/* ISSUE */}
      <section>
        <h2 className="text-lg font-bold text-white mb-3">Issue a sanction</h2>
        <Card padding="p-5">
          <IssueSanctionForm userId={user.userId} onIssued={load} />
        </Card>
      </section>

      {/* DETECTION */}
      <section>
        <h2 className="text-lg font-bold text-white mb-1">Detection</h2>
        <p className="text-sm text-muted mb-3">
          Advisory analysis, run on demand. Engine assistance is named-account
          only, typically after a report; there is no pool-wide engine scan.
          Neither detector has ever been validated against a confirmed cheat,
          and neither has any automatic effect on this account.
        </p>
        <div className="flex flex-col gap-4">
          <DetectionReportCard
            title="Sandbagging"
            fetchReports={() => getSandbaggingReport(user.userId)}
          />
          <DetectionReportCard
            title="Engine assistance"
            fetchReports={() => getEngineReport(user.userId).then((r) => [r])}
          />
        </div>
      </section>

      {lifting && (
        <LiftDialog
          sanction={lifting}
          onClose={() => setLifting(null)}
          onLifted={() => {
            setLifting(null);
            load();
          }}
        />
      )}
    </div>
  );
}

export default function ModerationUserPage() {
  const params = useParams<{ userId: string }>();
  const userId = params?.userId ?? "";

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
            <Link
              href="/moderation"
              className="inline-flex items-center gap-1.5 text-sm text-muted hover:text-[var(--bounty)] transition-colors mb-6"
            >
              <IconArrowLeft size={14} stroke={2} />
              Back to moderation
            </Link>
            <Dossier userId={userId} />
          </main>
        </RequireAuth>
      </StaffOnly>
      <Footer />
    </div>
  );
}
