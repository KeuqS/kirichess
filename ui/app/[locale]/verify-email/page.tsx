"use client";

import { Suspense, useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { AuthShell } from "@/components/AuthShell";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { confirmEmail, ApiError } from "@/lib/api";

type Status = "pending" | "success" | "error";

function VerifyEmailForm() {
  const t = useTranslations("verifyEmail");
  const params = useSearchParams();
  const userId = params.get("userId") ?? "";
  const token = params.get("token") ?? "";

  const [status, setStatus] = useState<Status>("pending");
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    if (!userId || !token) {
      setStatus("error");
      setMessage(t("missingUserIdOrToken"));
      return;
    }
    confirmEmail(userId, token)
      .then(() => setStatus("success"))
      .catch((err) => {
        setStatus("error");
        setMessage(err instanceof ApiError ? err.message : t("genericError"));
      });
  }, [userId, token, t]);

  return (
    <AuthShell brand={t("brand")} heading={t("pageTitle")} center>
      {status === "pending" && (
        <SkeletonStatus label={t("confirming")} count={2} />
      )}
      {status === "success" && (
        <p className="text-sm text-[var(--foreground)]">{t("confirmed")}</p>
      )}
      {status === "error" && <p className="error-banner">{message}</p>}

      <Link href="/login" className="btn-primary mt-6 w-full py-2.5 text-sm">
        {t("goToLogin")}
      </Link>
    </AuthShell>
  );
}

function VerifyEmailFallback() {
  const t = useTranslations("verifyEmail");
  return (
    <AuthShell brand={t("brand")} heading={t("pageTitle")} center>
      <SkeletonStatus label={t("confirming")} count={2} />
    </AuthShell>
  );
}

export default function VerifyEmailPage() {
  return (
    <Suspense fallback={<VerifyEmailFallback />}>
      <VerifyEmailForm />
    </Suspense>
  );
}
