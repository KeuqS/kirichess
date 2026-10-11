"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { IconCopy, IconX } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import {
  ApiError,
  createApiToken,
  getApiTokens,
  getTwoFactorStatus,
  revokeApiToken,
} from "@/lib/api";
import type { ApiTokenDto, CreatedApiTokenDto } from "@/lib/types";

type View = "loading" | "error" | "loaded";

function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

/**
 * Phase 9f.5 profile API-token card. Sibling of SecurityCard: these keys survive logout and 2FA
 * toggles on purpose, so the copy has to say so. Renders nothing without a token, same rule every
 * other live card on this page follows.
 */
export function ApiTokensCard() {
  const t = useTranslations("ApiTokensCard");
  const { token } = useAuth();

  const [view, setView] = useState<View>("loading");
  const [tokens, setTokens] = useState<ApiTokenDto[]>([]);
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [hasPassword, setHasPassword] = useState<boolean | null>(null);
  const [canRead, setCanRead] = useState(true);
  const [canPlay, setCanPlay] = useState(false);
  const [creating, setCreating] = useState(false);
  const [revokingId, setRevokingId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<CreatedApiTokenDto | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    setView("loading");
    setActionError(null);
    getApiTokens()
      .then((res) => {
        if (cancelled) return;
        setTokens(res);
        setView("loaded");
      })
      .catch((err) => {
        if (cancelled) return;
        setView("error");
        setActionError(
          err instanceof ApiError ? err.message : t("genericError"),
        );
      });
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
  }, [token, t]);

  if (!token) return null;

  const scopes: string[] = [];
  if (canRead) scopes.push("read");
  if (canPlay) scopes.push("play");

  async function handleCreate(e: FormEvent) {
    e.preventDefault();
    setActionError(null);
    if (hasPassword && password.trim() === "") {
      setActionError(t("passwordRequired"));
      return;
    }
    setCopied(false);
    setCreating(true);
    try {
      const created = await createApiToken(
        name.trim(),
        scopes,
        hasPassword ? password : undefined,
      );
      setTokens((prev) => [
        {
          id: created.id,
          name: created.name,
          tokenPrefix: created.tokenPrefix,
          scopes: created.scopes,
          createdAt: created.createdAt,
          expiresAt: created.expiresAt,
          lastUsedAt: created.lastUsedAt,
        },
        ...prev,
      ]);
      setRevealed(created);
      setName("");
      setPassword("");
      setCanRead(true);
      setCanPlay(false);
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setCreating(false);
    }
  }

  async function handleRevoke(id: string) {
    setActionError(null);
    setRevokingId(id);
    try {
      await revokeApiToken(id);
      setTokens((prev) => prev.filter((row) => row.id !== id));
      if (revealed?.id === id) setRevealed(null);
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setRevokingId(null);
    }
  }

  async function handleCopy() {
    if (!revealed?.token) return;
    try {
      await navigator.clipboard.writeText(revealed.token);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="mb-8">
      <h2 className="profile-category mb-4">{t("heading")}</h2>
      <Card>
        <p className="text-sm text-[var(--foreground)] leading-relaxed mb-4">
          {t("description")}
        </p>

        {view === "loading" && (
          <SkeletonStatus label={t("checking")} count={3} />
        )}

        {view === "loaded" && (
          <div className="flex flex-col">
            {tokens.map((row, i) => (
              <div
                key={row.id}
                className={`flex items-center justify-between gap-4 py-3 ${
                  i < tokens.length - 1 ? "border-b border-[var(--border)]/50" : ""
                }`}
              >
                <div className="min-w-0">
                  <div className="text-sm text-white truncate">{row.name}</div>
                  <div className="text-sm text-muted mt-0.5">
                    <code className="font-mono text-[var(--bounty)]">
                      kirichess_pat_{row.tokenPrefix}…
                    </code>
                    {" · "}
                    {row.scopes.map((scope, scopeIndex) => (
                      <span key={scope}>
                        {scopeIndex > 0 && ", "}
                        <code className="font-mono text-[var(--bounty)]">{scope}</code>
                      </span>
                    ))}
                    {" · "}
                    {t("created", { when: formatWhen(row.createdAt) })}
                    {row.expiresAt && (
                      <>
                        {" · "}
                        {t("expires", { when: formatWhen(row.expiresAt) })}
                      </>
                    )}
                  </div>
                </div>
                <button
                  onClick={() => handleRevoke(row.id)}
                  disabled={revokingId === row.id}
                  aria-label={t("revokeAriaLabel")}
                  className="btn-danger shrink-0 gap-1.5 px-3 py-1.5 text-sm"
                >
                  <IconX size={14} stroke={2} />
                  {revokingId === row.id ? "…" : t("revoke")}
                </button>
              </div>
            ))}

            {tokens.length === 0 && (
              <div className="text-sm text-muted">{t("none")}</div>
            )}

            {revealed?.token && (
              <div className="mt-4 notice-banner">
                <div className="text-sm font-semibold text-white mb-2">
                  {t("revealHeading")}
                </div>
                <p className="text-sm text-muted mb-2">{t("revealWarning")}</p>
                <div className="flex items-center gap-3">
                  <code className="flex-1 min-w-0 truncate font-mono text-sm text-[var(--bounty)]">
                    {revealed.token}
                  </code>
                  <button
                    type="button"
                    onClick={handleCopy}
                    className="btn-secondary shrink-0 gap-1.5 px-3 py-1.5 text-sm"
                  >
                    <IconCopy size={14} stroke={2} />
                    {copied ? t("copied") : t("copy")}
                  </button>
                </div>
              </div>
            )}

            <form
              onSubmit={handleCreate}
              className="mt-5 pt-5 border-t border-[var(--border)] flex flex-col gap-4"
            >
              <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
                {t("nameLabel")}
                <input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  maxLength={64}
                  required
                  className="border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
                />
              </label>
              {hasPassword && (
                <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
                  {t("passwordLabel")}
                  <input
                    type="password"
                    autoComplete="current-password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    className="border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
                  />
                </label>
              )}
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="checkbox-bounty"
                    checked={canRead}
                    onChange={(e) => setCanRead(e.target.checked)}
                  />
                  {t("scopeRead")}
                </label>
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="checkbox-bounty"
                    checked={canPlay}
                    onChange={(e) => setCanPlay(e.target.checked)}
                  />
                  {t("scopePlay")}
                </label>
              </div>
              <button
                type="submit"
                disabled={
                  creating ||
                  scopes.length === 0 ||
                  name.trim() === "" ||
                  (hasPassword === true && password.trim() === "")
                }
                className="btn-primary self-start px-5 py-2 text-sm"
              >
                {creating ? t("creating") : t("create")}
              </button>
            </form>

            <p className="text-sm text-muted mt-4">{t("footerNote")}</p>
          </div>
        )}

        {actionError && <div className="mt-3 error-banner">{actionError}</div>}
      </Card>
    </div>
  );
}
