"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { IconCheck, IconPencil, IconX } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { ApiError, changeDisplayName } from "@/lib/api";
import { useDisplayNameCheck } from "@/lib/useDisplayNameCheck";

type StatusKind = "cooldown" | "validation" | "generic";

const NAME_CLASS =
  "text-3xl font-bold leading-tight tracking-tight text-white text-balance";

/**
 * Phase 8a.4 rename control. Sits in the profile header beside the name: a pencil opens a
 * short inline field, and the page heading stays the name. Renders nothing without a token
 * so the signed-out view can keep its own guest heading.
 */
export function DisplayNameCard() {
  const t = useTranslations("DisplayNameCard");
  const { displayName, token, setDisplayName } = useAuth();
  const [editing, setEditing] = useState(false);
  const [candidate, setCandidate] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [status, setStatus] = useState<{
    kind: StatusKind;
    message: string;
  } | null>(null);

  // Advisory live feedback while the field is open: shares the exact same debounce+cancel
  // implementation the register form uses (TASK-D step 5: extract one hook, don't copy-paste).
  const { verdict } = useDisplayNameCheck(editing ? candidate : "");

  if (!token) return null;

  function startEditing() {
    setCandidate(displayName ?? "");
    setStatus(null);
    setEditing(true);
  }

  function cancelEditing() {
    setEditing(false);
    setStatus(null);
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const next = candidate.trim();
    if (next === (displayName ?? "")) {
      setEditing(false);
      setStatus(null);
      return;
    }
    setStatus(null);
    setSubmitting(true);
    try {
      const res = await changeDisplayName(next);
      setDisplayName(res.displayName);
      setEditing(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // The cooldown. The user did nothing wrong. They're early. Informational, not an error.
        setStatus({ kind: "cooldown", message: err.message });
      } else if (err instanceof ApiError && err.status === 400) {
        // A policy rejection: a real validation problem.
        setStatus({ kind: "validation", message: err.message });
      } else {
        setStatus({
          kind: "generic",
          message: err instanceof ApiError ? err.message : t("genericError"),
        });
      }
    } finally {
      setSubmitting(false);
    }
  }

  const trimmed = candidate.trim();
  const unchanged = trimmed === (displayName ?? "");
  // The availability check is anonymous, so the player's own current name comes back "taken".
  // That answer is only useful once they have actually typed something else.
  const showVerdict = Boolean(verdict && !unchanged);

  return (
    <div className="mb-4">
      {!editing ? (
        <div className="flex items-center gap-1">
          <h1 className={NAME_CLASS}>{displayName}</h1>
          <button
            type="button"
            onClick={startEditing}
            aria-label={t("change")}
            className="shrink-0 p-2 text-white hover:text-[var(--bounty)]"
          >
            <IconPencil size={18} stroke={2} />
          </button>
        </div>
      ) : (
        <form
          onSubmit={handleSubmit}
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.preventDefault();
              cancelEditing();
            }
          }}
          className="flex flex-col gap-2"
        >
          <h1 className="sr-only">{displayName}</h1>
          <div className="flex items-center gap-2">
            <input
              autoFocus
              aria-label={t("displayName")}
              value={candidate}
              onChange={(e) => setCandidate(e.target.value)}
              className="min-w-0 flex-1 max-w-xs border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
            />
            <button
              type="submit"
              disabled={submitting || trimmed === ""}
              aria-label={t("save")}
              title={t("save")}
              className="btn-primary shrink-0 p-2"
            >
              <IconCheck size={16} stroke={2.5} />
            </button>
            <button
              type="button"
              onClick={cancelEditing}
              aria-label={t("cancel")}
              title={t("cancel")}
              className="btn-secondary shrink-0 p-2"
            >
              <IconX size={16} stroke={2.5} />
            </button>
          </div>

          {/* Advisory only: never blocks submit. Never shows a moderator-only field. */}
          {showVerdict && verdict ? (
            <span
              className={`text-sm ${
                verdict.allowed ? "text-[var(--gain)]" : "text-[var(--loss)]"
              }`}
            >
              {verdict.allowed ? t("nameAvailable") : verdict.message}
            </span>
          ) : null}

          {status?.kind === "cooldown" && (
            <span className="text-sm text-muted">{status.message}</span>
          )}
          {status?.kind === "validation" && (
            <span className="text-sm text-[var(--loss)]">{status.message}</span>
          )}
          {status?.kind === "generic" && (
            <div className="error-banner">{status.message}</div>
          )}
        </form>
      )}
    </div>
  );
}
