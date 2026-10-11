"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { PushNotificationToggle } from "@/components/PushNotificationToggle";
import { useAuth } from "@/components/AuthProvider";
import {
  ApiError,
  getNotificationPreferences,
  updateNotificationPreference,
} from "@/lib/api";
import type {
  NotificationCategory,
  NotificationPreferencesDto,
} from "@/lib/types";

const CATEGORY_LABEL_KEY: Record<NotificationCategory, string> = {
  PairingFound: "pairingFoundLabel",
  RematchOffered: "rematchOfferedLabel",
};

/**
 * Profile notifications section: per-category mail toggles, plus the device push row when this
 * browser can receive it. The mail half is the signed-in-visit equivalent of the one-click link
 * every governed email carries (`ui/app/unsubscribe/page.tsx`), which lands on `#notifications`.
 * Renders nothing when signed out, the same convention as every other card on this page.
 */
export function NotificationPreferencesCard() {
  const t = useTranslations("NotificationPreferencesCard");
  const { token } = useAuth();
  const [preferences, setPreferences] =
    useState<NotificationPreferencesDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<NotificationCategory | null>(null);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    getNotificationPreferences()
      .then((data) => {
        if (!cancelled) setPreferences(data);
      })
      .catch((err) => {
        if (!cancelled) {
          setError(err instanceof ApiError ? err.message : t("genericError"));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [token, t]);

  if (!token) return null;

  async function handleToggle(
    category: NotificationCategory,
    subscribed: boolean,
  ) {
    setError(null);
    setPending(category);
    // Optimistic: a mail preference toggle is low-stakes, and waiting on the round trip before the
    // checkbox visibly moves reads as broken on a slow connection. Reverted in the catch block below.
    setPreferences((prev) =>
      prev
        ? {
            categories: prev.categories.map((c) =>
              c.category === category ? { ...c, subscribed } : c,
            ),
          }
        : prev,
    );
    try {
      await updateNotificationPreference(category, subscribed);
    } catch (err) {
      setPreferences((prev) =>
        prev
          ? {
              categories: prev.categories.map((c) =>
                c.category === category ? { ...c, subscribed: !subscribed } : c,
              ),
            }
          : prev,
      );
      setError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setPending(null);
    }
  }

  return (
    <div id="notifications" className="mb-8 scroll-mt-24">
      <h2 className="profile-category mb-4">{t("heading")}</h2>
      <Card>
        <div className="flex flex-col gap-3">
          {error && <div className="error-banner">{error}</div>}

          {preferences ? (
            preferences.categories.map((c) => (
              <label
                key={c.category}
                className="flex items-center justify-between gap-4 cursor-pointer"
              >
                <span className="text-sm text-white">
                  {t(CATEGORY_LABEL_KEY[c.category])}
                </span>
                <input
                  type="checkbox"
                  checked={c.subscribed}
                  disabled={pending === c.category}
                  onChange={(e) => handleToggle(c.category, e.target.checked)}
                  className="checkbox-bounty disabled:opacity-50"
                />
              </label>
            ))
          ) : (
            !error && <SkeletonStatus label={t("loading")} count={2} />
          )}

          <PushNotificationToggle />
        </div>
      </Card>
    </div>
  );
}
