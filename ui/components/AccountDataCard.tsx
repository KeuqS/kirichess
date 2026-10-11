"use client";

import { useEffect, useState } from "react";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconDownload, IconTrash } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import {
  ApiError,
  deleteAccount,
  exportAccountData,
  getTwoFactorStatus,
} from "@/lib/api";
import type { AccountExportDto } from "@/lib/types";

type DeleteState = "idle" | "confirming" | "deleting";

/** The server's own check (`AccountEndpoints.MapAccountEndpoints`) is ordinal, untrimmed,
 * un-lowercased: this mirrors it exactly so the button disables/enables at the same boundary the
 * server will actually accept. */
const ACKNOWLEDGEMENT = "DELETE MY ACCOUNT";

type DeleteStatusKind = "live-game" | "mismatch" | "generic";

/**
 * Phase 8a.2 profile GDPR card. A sibling of `ui/components/DisplayNameCard.tsx` on the same
 * static-mock profile page: this is the second (and, for now, last) part of that page wired to the
 * real account. It renders nothing without a token so the mock page still works signed-out.
 */
export function AccountDataCard() {
  const t = useTranslations("AccountDataCard");
  const { token, logout } = useAuth();
  const router = useRouter();

  const [exporting, setExporting] = useState(false);
  const [exportSummary, setExportSummary] = useState<AccountExportDto | null>(
    null,
  );
  const [exportError, setExportError] = useState<string | null>(null);

  const [deleteState, setDeleteState] = useState<DeleteState>("idle");
  const [acknowledgement, setAcknowledgement] = useState("");
  const [password, setPassword] = useState("");
  const [hasPassword, setHasPassword] = useState<boolean | null>(null);
  const [deleteStatus, setDeleteStatus] = useState<{
    kind: DeleteStatusKind;
    message: string;
  } | null>(null);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    getTwoFactorStatus()
      .then((res) => {
        if (!cancelled) setHasPassword(res.hasPassword);
      })
      .catch(() => {
        // Do not guess. Hide the field until we know; the server still enforces.
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  if (!token) return null;

  const privacyNote = (
    <p className="text-sm text-muted m-0">
      {t.rich("seePrivacyPolicy", {
        privacy: (chunks) => (
          <Link
            href="/privacy"
            className="text-[var(--bounty)] font-medium hover:underline"
          >
            {chunks}
          </Link>
        ),
      })}
    </p>
  );

  async function handleExport() {
    setExporting(true);
    setExportError(null);
    setExportSummary(null);
    try {
      const { blob, filename } = await exportAccountData();

      // Read the payload for the on-screen summary before handing the same blob to the browser's
      // save dialog: the server already did the work of computing the counts, no reason to guess.
      const parsed = JSON.parse(await blob.text()) as AccountExportDto;
      setExportSummary(parsed);

      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = filename;
      a.click();
      // Not optional: without this the blob stays pinned for the lifetime of the document.
      URL.revokeObjectURL(url);
    } catch (err) {
      setExportError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setExporting(false);
    }
  }

  function startConfirming() {
    setAcknowledgement("");
    setPassword("");
    setDeleteStatus(null);
    setDeleteState("confirming");
  }

  function cancelConfirming() {
    setAcknowledgement("");
    setPassword("");
    setDeleteStatus(null);
    setDeleteState("idle");
  }

  async function handleDelete() {
    setDeleteStatus(null);
    if (hasPassword && password.trim() === "") {
      setDeleteStatus({
        kind: "mismatch",
        message: t("passwordRequired"),
      });
      return;
    }
    setDeleteState("deleting");
    try {
      await deleteAccount(
        acknowledgement,
        hasPassword ? password : undefined,
      );
      // Clear the session the same way the app's existing sign-out does: do not clear storage by
      // hand. logout() itself navigates to /login; the explicit push below sends the now-deleted
      // account to the home page instead, which is the more honest destination for this flow.
      logout();
      router.push("/");
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // The live-game refusal (README scope decision 3). The user did nothing wrong. They're
        // mid-game. Informational, not an error, same treatment DisplayNameCard gives the rename
        // cooldown.
        setDeleteStatus({ kind: "live-game", message: err.message });
      } else if (err instanceof ApiError && err.status === 400) {
        // The acknowledgement did not match server-side. Should be unreachable given the client-side
        // exact-match gate below, but the server is the authority.
        setDeleteStatus({ kind: "mismatch", message: err.message });
      } else {
        setDeleteStatus({
          kind: "generic",
          message: err instanceof ApiError ? err.message : t("genericError"),
        });
      }
      setDeleteState("confirming");
    }
  }

  return (
    <div className="mb-8">
      <h2 className="profile-category mb-4">{t("yourData")}</h2>
      <Card>
        <div className="flex items-center justify-between gap-4">
          <div className="text-sm text-white">{t("downloadYourData")}</div>
          <button
            onClick={handleExport}
            disabled={exporting}
            className="btn-secondary shrink-0 gap-1.5 px-3 py-1.5 text-sm"
          >
            <IconDownload size={14} stroke={2} />
            {exporting ? t("preparing") : t("export")}
          </button>
        </div>

        {exportSummary && (
          <div className="mt-3 text-sm text-muted">
            {t("exportSummary", {
              games: exportSummary.games.length,
              reports: exportSummary.reportsFiled.length,
              sanctions: exportSummary.sanctions.length,
            })}
            {exportSummary.truncation.gamesTruncated && (
              <div className="text-muted mt-0.5">
                {t("truncationNote", {
                  cap: exportSummary.truncation.gamesCap,
                })}
              </div>
            )}
          </div>
        )}

        {exportError && <div className="mt-3 error-banner">{exportError}</div>}

        <div className="mt-5 pt-5 border-t border-[var(--border)]">
          {deleteState === "idle" && (
            <div className="flex items-center justify-between gap-4">
              <div className="flex flex-col gap-1 min-w-0">
                <div className="text-sm text-white">{t("deleteYourAccount")}</div>
                {privacyNote}
              </div>
              <button
                onClick={startConfirming}
                className="btn-danger shrink-0 gap-1.5 px-3 py-1.5 text-sm"
              >
                <IconTrash size={14} stroke={2} />
                {t("deleteAccountButton")}
              </button>
            </div>
          )}

          {(deleteState === "confirming" || deleteState === "deleting") && (
            <div className="flex flex-col gap-3">
              <div className="text-sm text-white font-medium">
                {t("willErase")}
              </div>
              <ul className="text-sm text-muted list-disc pl-4 space-y-1">
                <li>{t("noGracePeriod")}</li>
                <li>{t("attributedToDeleted")}</li>
                <li>{t("sanctionsRemain")}</li>
              </ul>

              <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
                {t("typeToConfirm", { phrase: ACKNOWLEDGEMENT })}
                <input
                  autoFocus
                  value={acknowledgement}
                  onChange={(e) => setAcknowledgement(e.target.value)}
                  disabled={deleteState === "deleting"}
                  className="border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-white focus:border-[var(--loss)] focus:outline-none disabled:opacity-50"
                />
              </label>

              {hasPassword === null ? (
                <SkeletonStatus
                  label={t("passwordLabel")}
                  count={2}
                  widths={["40%", "100%"]}
                />
              ) : hasPassword ? (
                <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
                  {t("passwordLabel")}
                  <input
                    type="password"
                    autoComplete="current-password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    disabled={deleteState === "deleting"}
                    className="border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-white focus:border-[var(--loss)] focus:outline-none disabled:opacity-50"
                  />
                </label>
              ) : null}

              <div className="flex items-center gap-2">
                <button
                  onClick={handleDelete}
                  disabled={
                    acknowledgement !== ACKNOWLEDGEMENT ||
                    deleteState === "deleting" ||
                    (hasPassword === true && password.trim() === "")
                  }
                  className="btn-danger-fill flex-1 py-2.5 text-sm"
                >
                  {deleteState === "deleting"
                    ? t("deleting")
                    : t("permanentlyDeleteAccount")}
                </button>
                <button
                  type="button"
                  onClick={cancelConfirming}
                  disabled={deleteState === "deleting"}
                  className="btn-secondary flex-1 py-2.5 text-sm"
                >
                  {t("cancel")}
                </button>
              </div>

              {deleteStatus?.kind === "live-game" && (
                <span className="text-sm text-muted">
                  {deleteStatus.message}
                </span>
              )}
              {deleteStatus?.kind === "mismatch" && (
                <span className="text-sm text-[var(--loss)]">
                  {deleteStatus.message}
                </span>
              )}
              {deleteStatus?.kind === "generic" && (
                <div className="error-banner">{deleteStatus.message}</div>
              )}

              {privacyNote}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
