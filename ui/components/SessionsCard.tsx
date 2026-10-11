"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { IconLogout, IconX } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { ApiError, getSessions, revokeSession } from "@/lib/api";
import type { SessionDto } from "@/lib/types";

type View = "loading" | "error" | "loaded";

function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

/**
 * Phase 9e.6 profile session-management section. Nested in `SecurityCard` with `TwoFactorCard`.
 * 6b.1 already built the rows (refresh-token rotation, reuse detection, `/logout-all`); this is
 * where a signed-in player finally sees them. Renders nothing without a token, same rule every
 * other live card on this page follows.
 */
export function SessionsCard() {
  const t = useTranslations("SessionsCard");
  const { token, signOutEverywhere } = useAuth();

  const [view, setView] = useState<View>("loading");
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [revokingId, setRevokingId] = useState<string | null>(null);
  const [signingOutEverywhere, setSigningOutEverywhere] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    setView("loading");
    getSessions()
      .then((res) => {
        if (cancelled) return;
        setSessions(res);
        setView("loaded");
      })
      .catch(() => {
        if (cancelled) return;
        setView("error");
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  if (!token) return null;
  if (view === "error") return null;

  async function handleRevoke(id: string) {
    setActionError(null);
    setRevokingId(id);
    try {
      await revokeSession(id);
      setSessions((prev) => prev.filter((s) => s.id !== id));
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setRevokingId(null);
    }
  }

  async function handleSignOutEverywhere() {
    setActionError(null);
    setSigningOutEverywhere(true);
    try {
      await signOutEverywhere();
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("genericError"));
      setSigningOutEverywhere(false);
    }
  }

  return (
    <div className="mt-5 pt-5 border-t border-[var(--border)]">
      <div className="flex items-center justify-between gap-4 mb-3">
        <div className="text-sm text-white">{t("sectionTitle")}</div>
        {view === "loaded" && sessions.length > 1 && (
          <button
            onClick={handleSignOutEverywhere}
            disabled={signingOutEverywhere}
            className="btn-danger shrink-0 gap-1.5 px-3 py-1.5 text-sm"
          >
            <IconLogout size={14} stroke={2} />
            {signingOutEverywhere ? t("signingOut") : t("signOutEverywhere")}
          </button>
        )}
      </div>

      {view === "loading" && <SkeletonStatus label={t("checking")} count={3} />}

      {view === "loaded" && (
        <div className="flex flex-col">
          {sessions.map((s, i) => (
            <div
              key={s.id}
              className={`flex items-center justify-between gap-4 py-3 ${
                i < sessions.length - 1 ? "border-b border-[var(--border)]/50" : ""
              }`}
            >
              <div className="min-w-0">
                <div className="text-sm text-white">
                  {t("signedIn", { when: formatWhen(s.createdAt) })}
                  {s.isCurrent && (
                    <span className="text-muted"> · {t("thisDevice")}</span>
                  )}
                </div>
                <div className="text-sm text-muted mt-0.5">
                  {t("expires", { when: formatWhen(s.expiresAt) })}
                </div>
              </div>
              {!s.isCurrent && (
                <button
                  onClick={() => handleRevoke(s.id)}
                  disabled={revokingId === s.id}
                  aria-label={t("revokeAriaLabel")}
                  className="btn-danger shrink-0 gap-1.5 px-3 py-1.5 text-sm"
                >
                  <IconX size={14} stroke={2} />
                  {revokingId === s.id ? "…" : t("revoke")}
                </button>
              )}
            </div>
          ))}

          {sessions.length === 0 && (
            <div className="text-sm text-muted">{t("noActiveSessions")}</div>
          )}

          <p className="text-sm text-muted mt-4">{t("footerNote")}</p>
        </div>
      )}

      {actionError && <div className="mt-3 error-banner">{actionError}</div>}
    </div>
  );
}
