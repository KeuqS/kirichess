"use client";

import { useState, type FormEvent } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { AuthShell } from "@/components/AuthShell";
import { forgotPassword } from "@/lib/api";

export default function ForgotPasswordPage() {
  const t = useTranslations("forgotPassword");
  const [email, setEmail] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    try {
      await forgotPassword(email);
    } finally {
      // Always show the same success state: the backend never reveals whether the email exists.
      setSent(true);
      setSubmitting(false);
    }
  }

  return (
    <AuthShell brand={t("brand")} heading={t("pageTitle")}>
      {sent ? (
        <div className="flex flex-col gap-5 text-center">
          <p className="text-sm text-[var(--foreground)]">{t("sentMessage")}</p>
          <Link
            href="/login"
            className="btn-primary mt-2 w-full py-2.5 text-sm"
          >
            {t("backToLogin")}
          </Link>
        </div>
      ) : (
        <form onSubmit={handleSubmit} className="flex flex-col gap-5">
          <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
            {t("emailLabel")}
            <input
              type="email"
              className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </label>
          <div className="mt-4 flex flex-col gap-3">
            <button
              type="submit"
              disabled={submitting}
              className="btn-primary w-full py-2.5 text-sm"
            >
              {submitting ? t("pleaseWait") : t("sendResetLink")}
            </button>
            <Link href="/login" className="btn-secondary w-full py-2.5 text-sm">
              {t("backToLogin")}
            </Link>
          </div>
        </form>
      )}
    </AuthShell>
  );
}
