"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, completeTwoFactorLogin } from "@/lib/api";
import type { AuthResponse } from "@/lib/types";

interface TwoFactorPromptProps {
  ticket: string;
  onSuccess: (auth: AuthResponse) => void;
  /**
   * A 423 lockout: the server's own text is shown by the caller, and there is nothing useful to do
   * on this form for the next fifteen minutes, so the caller drops back to whatever came before it.
   */
  onLockout: (message: string) => void;
  /** Omit to hide the Back control: the OAuth callback has no password step to return to. */
  onBack?: () => void;
}

/**
 * Phase 9d.2. The second step of a two-factor login, shared by `ui/app/login/page.tsx` (the normal
 * password path) and `ui/app/auth/callback/page.tsx` (a linked Google account can still hit a
 * challenge: a second factor on the account must not be bypassable through OAuth). A wrong code
 * (401) does not burn the ticket server-side, so this stays on the code form rather than sending the
 * user back to the start; a lockout (423) hands the message to the caller instead, since there is
 * nothing left to do here for the next fifteen minutes.
 */
export function TwoFactorPrompt({
  ticket,
  onSuccess,
  onLockout,
  onBack,
}: TwoFactorPromptProps) {
  const t = useTranslations("twoFactorPrompt");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const res = await completeTwoFactorLogin(ticket, code);
      onSuccess(res);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        // The ticket survives a wrong code: stay on this form, just clear the input.
        setError(t("wrongCode"));
        setCode("");
      } else if (err instanceof ApiError && err.status === 423) {
        onLockout(err.message);
      } else {
        setError(err instanceof ApiError ? err.message : t("somethingWentWrong"));
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-5">
      <div>
        <h2 className="text-lg font-bold text-white">{t("title")}</h2>
        <p className="text-sm text-muted mt-1">{t("instruction")}</p>
      </div>

      <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
        {t("code")}
        <input
          inputMode="numeric"
          autoComplete="one-time-code"
          autoFocus
          maxLength={11}
          value={code}
          onChange={(e) => setCode(e.target.value)}
          className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none"
          required
        />
      </label>

      {error && (
        <p role="alert" className="error-banner">
          {error}
        </p>
      )}

      <div className="flex items-center gap-3">
        {onBack && (
          <button
            type="button"
            onClick={onBack}
            className="btn-secondary flex-1 py-2.5 text-sm"
          >
            {t("back")}
          </button>
        )}
        <button
          type="submit"
          disabled={submitting || code.trim() === ""}
          className="btn-primary flex-1 py-2.5 text-sm"
        >
          {submitting ? t("verifying") : t("verify")}
        </button>
      </div>
    </form>
  );
}
