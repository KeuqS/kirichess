"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import {
  ApiError,
  getVapidPublicKey,
  subscribeToPush,
  unsubscribeFromPush,
} from "@/lib/api";

/**
 * VAPID public keys arrive base64url-encoded; `PushManager.subscribe` wants raw bytes.
 * Built from an explicit `ArrayBuffer`, not `new Uint8Array(length)`: the latter types as
 * `Uint8Array<ArrayBufferLike>`, which `applicationServerKey`'s `BufferSource` rejects under this
 * project's TS/DOM lib version.
 */
function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const base64Safe = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(base64Safe);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

type Status = "checking" | "unavailable" | "off" | "on";

/**
 * The device row inside `NotificationPreferencesCard`. Renders nothing when signed out, when the
 * browser lacks Push API support, when the server has no VAPID key pair configured
 * (`VapidPublicKeyDto.enabled` false), or when no service worker is registered.
 * `ServiceWorkerRegistration` only registers `/sw.js` in production, so this row is intentionally
 * invisible in local dev rather than offering a toggle that would hang on `pushManager.subscribe`
 * with nothing to subscribe through.
 */
export function PushNotificationToggle() {
  const t = useTranslations("PushNotificationToggle");
  const { token } = useAuth();
  const [status, setStatus] = useState<Status>("checking");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [publicKey, setPublicKey] = useState<string | null>(null);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;

    async function check() {
      if (!("serviceWorker" in navigator) || !("PushManager" in window)) {
        if (!cancelled) setStatus("unavailable");
        return;
      }
      try {
        const vapid = await getVapidPublicKey();
        if (!vapid.enabled || !vapid.publicKey) {
          if (!cancelled) setStatus("unavailable");
          return;
        }

        // getRegistration, not .ready: .ready never resolves if no worker was ever registered
        // (exactly the dev-mode case), and this must not hang the profile page.
        const registration = await navigator.serviceWorker.getRegistration();
        if (!registration) {
          if (!cancelled) setStatus("unavailable");
          return;
        }

        if (cancelled) return;
        setPublicKey(vapid.publicKey);
        const subscription = await registration.pushManager.getSubscription();
        if (!cancelled) setStatus(subscription ? "on" : "off");
      } catch {
        if (!cancelled) setStatus("unavailable");
      }
    }

    check();
    return () => {
      cancelled = true;
    };
  }, [token]);

  if (!token || status === "checking" || status === "unavailable") return null;

  async function handleToggle() {
    setError(null);
    setPending(true);
    try {
      const registration = await navigator.serviceWorker.getRegistration();
      if (!registration) {
        setError(t("genericError"));
        return;
      }

      if (status === "on") {
        const subscription = await registration.pushManager.getSubscription();
        if (subscription) {
          await unsubscribeFromPush(subscription.endpoint);
          await subscription.unsubscribe();
        }
        setStatus("off");
        return;
      }

      const permission = await Notification.requestPermission();
      if (permission !== "granted") {
        setError(t("permissionDenied"));
        return;
      }

      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(publicKey!),
      });
      const json = subscription.toJSON();
      if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth) {
        throw new Error("Push subscription is missing required fields.");
      }
      await subscribeToPush({
        endpoint: json.endpoint,
        p256dh: json.keys.p256dh,
        auth: json.keys.auth,
      });
      setStatus("on");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setPending(false);
    }
  }

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <label className="flex items-center justify-between gap-4 cursor-pointer">
        <span className="text-sm text-white">{t("toggleLabel")}</span>
        <input
          type="checkbox"
          checked={status === "on"}
          disabled={pending}
          onChange={handleToggle}
          className="checkbox-bounty disabled:opacity-50"
        />
      </label>
    </>
  );
}
