"use client";

import { Suspense, useState, type FormEvent } from "react";
import { useSearchParams } from "next/navigation";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconEye, IconEyeOff } from "@tabler/icons-react";
import { AuthShell } from "@/components/AuthShell";
import { resetPassword, ApiError } from "@/lib/api";

function ResetPasswordForm() {
  const t = useTranslations("resetPassword");
  const router = useRouter();
  const params = useSearchParams();
  const email = params.get("email") ?? "";
  const token = params.get("token") ?? "";

  const [newPassword, setNewPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [done, setDone] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (newPassword.length < 8) {
      setError(t("passwordTooShort"));
      return;
    }
    if (!email || !token) {
      setError(t("missingEmailOrToken"));
      return;
    }

    setSubmitting(true);
    try {
      await resetPassword(email, token, newPassword);
      setDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthShell brand={t("brand")} heading={t("resetPasswordCta")}>
      {done ? (
        <div className="flex flex-col gap-5 text-center">
          <p className="text-sm text-[var(--foreground)]">{t("doneMessage")}</p>
          <button
            onClick={() => router.push("/login")}
            className="btn-primary mt-2 w-full py-2.5 text-sm"
          >
            {t("goToLogin")}
          </button>
        </div>
      ) : (
        <form onSubmit={handleSubmit} className="flex flex-col gap-5">
          <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
            {t("newPasswordLabel")}
            <div className="relative flex">
              <input
                type={showPassword ? "text" : "password"}
                className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 pr-10 text-white focus:border-[var(--bounty)] focus:outline-none"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                required
                minLength={8}
              />
              <button
                type="button"
                onClick={() => setShowPassword((prev) => !prev)}
                className="absolute right-0 top-0 flex h-full items-center px-3 text-muted hover:text-[var(--bounty)] focus:outline-none transition-colors"
                tabIndex={-1}
                aria-label={
                  showPassword ? t("hidePassword") : t("showPassword")
                }
              >
                {showPassword ? (
                  <IconEyeOff size={16} stroke={2} />
                ) : (
                  <IconEye size={16} stroke={2} />
                )}
              </button>
            </div>
            <span className="text-sm text-muted mt-0.5">
              {t("passwordRequirements")}
            </span>
          </label>

          {error && <p className="error-banner">{error}</p>}

          <div className="mt-4 flex flex-col gap-3">
            <button
              type="submit"
              disabled={submitting}
              className="btn-primary w-full py-2.5 text-sm"
            >
              {submitting ? t("pleaseWait") : t("resetPasswordCta")}
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

export default function ResetPasswordPage() {
  return (
    <Suspense fallback={null}>
      <ResetPasswordForm />
    </Suspense>
  );
}
