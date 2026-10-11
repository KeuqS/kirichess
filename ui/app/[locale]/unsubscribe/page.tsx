"use client";

import { Suspense, useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { AuthShell } from "@/components/AuthShell";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { unsubscribeFromNotifications, ApiError } from "@/lib/api";

type Status = "pending" | "success" | "error";

function UnsubscribeForm() {
  const t = useTranslations("unsubscribe");
  const params = useSearchParams();
  const userId = params.get("userId") ?? "";
  const category = params.get("category") ?? "";
  const token = params.get("token") ?? "";

  const [status, setStatus] = useState<Status>("pending");
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    if (!userId || !category || !token) {
      setStatus("error");
      setMessage(t("missingParams"));
      return;
    }
    unsubscribeFromNotifications(userId, category, token)
      .then(() => setStatus("success"))
      .catch((err) => {
        setStatus("error");
        setMessage(err instanceof ApiError ? err.message : t("genericError"));
      });
  }, [userId, category, token, t]);

  return (
    <AuthShell brand={t("brand")} heading={t("metaTitle")} center>
      {status === "pending" && (
        <SkeletonStatus label={t("unsubscribing")} count={2} />
      )}
      {status === "success" && (
        <>
          <p className="text-sm text-[var(--foreground)]">
            {t("unsubscribed")}
          </p>
          <p className="mt-3 text-sm text-muted">{t("resubscribeHint")}</p>
        </>
      )}
      {status === "error" && <p className="error-banner">{message}</p>}

      <div className="mt-6 flex flex-col gap-3">
        {status === "success" && (
          <Link
            href="/profile#notifications"
            className="btn-secondary w-full py-2.5 text-sm"
          >
            {t("managePreferences")}
          </Link>
        )}
        <Link href="/" className="btn-primary w-full py-2.5 text-sm">
          {t("backToKiriChess")}
        </Link>
      </div>
    </AuthShell>
  );
}

function UnsubscribeFallback() {
  const t = useTranslations("unsubscribe");
  return (
    <AuthShell brand={t("brand")} heading={t("metaTitle")} center>
      <SkeletonStatus label={t("unsubscribing")} count={2} />
    </AuthShell>
  );
}

export default function UnsubscribePage() {
  return (
    <Suspense fallback={<UnsubscribeFallback />}>
      <UnsubscribeForm />
    </Suspense>
  );
}
