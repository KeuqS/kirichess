"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { toString as qrCodeToString } from "qrcode";
import {
  IconBrandGoogle,
  IconCopy,
  IconKey,
  IconShieldLock,
} from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { SkeletonStatus } from "@/components/SkeletonRows";
import {
  ApiError,
  beginTwoFactorEnrolment,
  disableTwoFactor,
  enableTwoFactor,
  getTwoFactorStatus,
  googleSignInUrl,
  regenerateRecoveryCodes,
} from "@/lib/api";
import type { TwoFactorEnrolment, TwoFactorStatus } from "@/lib/types";

type View = "loading" | "off" | "enrolling" | "codes" | "on" | "verifying";

/** Which action put the card into `verifying`: decides what `handleVerify` calls and what happens
 * next, since both disable and regenerate share the same "one code, then act" shape. */
type VerifyAction = "disable" | "regenerate";

const GOOGLE_REAUTH_CODE_KEY = "kirichess.googleReauthCode";
const PENDING_ENROLMENT_KEY = "kirichess.pendingTwoFactorEnrolment";

const INPUT_CLASS =
  "w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none";

/** Groups a raw base32 key into fours for display only. The value sent to the server (and copied to
 * the clipboard) is always the ungrouped `sharedKey` string: see README § 6.3. */
function groupKey(key: string): string {
  return key.replace(/(.{4})/g, "$1 ").trim();
}

/**
 * Phase 9d.2 profile 2FA section. Nested in `SecurityCard` with `SessionsCard`. Renders nothing
 * without a token so the mock page still works signed-out, and renders nothing if the initial
 * status check fails (same rule `SanctionBanner` follows: a profile page must not break because
 * one call failed).
 */
export function TwoFactorCard() {
  const t = useTranslations("TwoFactorCard");
  const { token, adoptSession } = useAuth();

  const [view, setView] = useState<View>("loading");
  const [statusError, setStatusError] = useState(false);
  const [recoveryCodesRemaining, setRecoveryCodesRemaining] = useState(0);
  const [hasPassword, setHasPassword] = useState<boolean | null>(null);

  const [enrolment, setEnrolment] = useState<TwoFactorEnrolment | null>(null);
  const [qrCodeSvg, setQrCodeSvg] = useState<string | null>(null);
  const [enrolCode, setEnrolCode] = useState("");
  const [enrolPassword, setEnrolPassword] = useState("");
  const [googleReauthCode, setGoogleReauthCode] = useState<string | null>(null);
  const [enrolError, setEnrolError] = useState<string | null>(null);
  const [enrolSubmitting, setEnrolSubmitting] = useState(false);
  const [copiedKey, setCopiedKey] = useState(false);

  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);
  const [copiedCodes, setCopiedCodes] = useState(false);

  const [verifyAction, setVerifyAction] = useState<VerifyAction | null>(null);
  const [verifyCode, setVerifyCode] = useState("");
  const [verifyPassword, setVerifyPassword] = useState("");
  const [verifyError, setVerifyError] = useState<string | null>(null);
  const [verifySubmitting, setVerifySubmitting] = useState(false);

  function clearGoogleReauth() {
    sessionStorage.removeItem(GOOGLE_REAUTH_CODE_KEY);
    sessionStorage.removeItem(PENDING_ENROLMENT_KEY);
    setGoogleReauthCode(null);
  }

  // C-10-10: loadStatus is called both on mount and again from handleDismissCodes, and the
  // component fetches unconditionally without a token guard. requestIdRef supersedes an
  // in-flight call whenever a newer one starts (token change re-running the effect, or a manual
  // reload racing a slow initial one), so a stale response from an earlier call cannot overwrite
  // state a newer call already set - the same guarantee MatchHistoryCard's `cancelled` flag gives
  // its own on-mount fetch, generalised here for loadStatus's two call sites.
  const requestIdRef = useRef(0);

  function loadStatus() {
    const requestId = ++requestIdRef.current;
    getTwoFactorStatus()
      .then((res: TwoFactorStatus) => {
        if (requestIdRef.current !== requestId) return;
        setRecoveryCodesRemaining(res.recoveryCodesRemaining);
        setHasPassword(res.hasPassword);
        if (res.enabled) {
          setView("on");
          return;
        }
        const storedReauth = sessionStorage.getItem(GOOGLE_REAUTH_CODE_KEY);
        const pendingRaw = sessionStorage.getItem(PENDING_ENROLMENT_KEY);
        if (storedReauth && pendingRaw) {
          try {
            const pending = JSON.parse(pendingRaw) as TwoFactorEnrolment & {
              enrolCode?: string;
            };
            if (pending.sharedKey && pending.otpauthUri) {
              setEnrolment({
                sharedKey: pending.sharedKey,
                otpauthUri: pending.otpauthUri,
              });
              setEnrolCode(pending.enrolCode ?? "");
              setGoogleReauthCode(storedReauth);
              setView("enrolling");
              return;
            }
          } catch {
            sessionStorage.removeItem(PENDING_ENROLMENT_KEY);
          }
        }
        setView("off");
      })
      .catch(() => {
        if (requestIdRef.current !== requestId) return;
        setStatusError(true);
      });
  }

  useEffect(() => {
    if (!token) return;
    loadStatus();
  }, [token]);

  // Renders the enrolment key as a scannable QR code so setup doesn't require typing a
  // 20+ character base32 string by hand - the common case is scanning from a phone while this
  // card is open on a different device. SVG rather than <canvas>: it's pure string generation
  // (no DOM canvas context needed, so it also works under jsdom in tests), and it stays crisp
  // at any display size. otpauthUri already carries the shared key (see TwoFactorEndpoints.cs's
  // BuildOtpauthUri), so this reveals nothing beyond what the setup-key text below already shows.
  useEffect(() => {
    if (!enrolment) {
      setQrCodeSvg(null);
      return;
    }
    let cancelled = false;
    qrCodeToString(enrolment.otpauthUri, { type: "svg", margin: 1, width: 200 })
      .then((svg) => {
        if (!cancelled) setQrCodeSvg(svg);
      })
      .catch(() => {
        // No QR is not a dead end: the setup key and its copy button still work.
        if (!cancelled) setQrCodeSvg(null);
      });
    return () => {
      cancelled = true;
    };
  }, [enrolment]);

  if (!token) return null;
  if (statusError) return null;

  async function handleEnable() {
    setEnrolError(null);
    setEnrolSubmitting(true);
    try {
      const res = await beginTwoFactorEnrolment();
      setEnrolment(res);
      setEnrolCode("");
      setEnrolPassword("");
      setView("enrolling");
    } catch (err) {
      setEnrolError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setEnrolSubmitting(false);
    }
  }

  async function handleConfirm(e: FormEvent) {
    e.preventDefault();
    setEnrolError(null);
    if (hasPassword) {
      if (enrolPassword.trim() === "") {
        setEnrolError(t("passwordRequired"));
        return;
      }
    } else if (!googleReauthCode) {
      return;
    }
    setEnrolSubmitting(true);
    try {
      const res = await enableTwoFactor(
        enrolCode,
        hasPassword
          ? { password: enrolPassword }
          : { googleReauthCode: googleReauthCode ?? undefined },
      );
      // The old token pair is dead the moment the server responds: every other tab's session is
      // gone too. Adopt the new one before showing the recovery codes.
      adoptSession(res.auth);
      clearGoogleReauth();
      setRecoveryCodes(res.recoveryCodes);
      setCopiedCodes(false);
      setView("codes");
    } catch (err) {
      setEnrolError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setEnrolSubmitting(false);
    }
  }

  function startGoogleReauth() {
    if (!enrolment) return;
    sessionStorage.setItem(
      PENDING_ENROLMENT_KEY,
      JSON.stringify({
        sharedKey: enrolment.sharedKey,
        otpauthUri: enrolment.otpauthUri,
        enrolCode,
      }),
    );
    window.location.assign(googleSignInUrl("/profile?reauth=2fa"));
  }

  function handleCancelEnrolment() {
    setEnrolment(null);
    setEnrolCode("");
    setEnrolPassword("");
    setEnrolError(null);
    clearGoogleReauth();
    setView("off");
  }

  function handleDismissCodes() {
    setRecoveryCodes([]);
    setView("loading");
    loadStatus();
  }

  function startVerifying(action: VerifyAction) {
    setVerifyAction(action);
    setVerifyCode("");
    setVerifyPassword("");
    setVerifyError(null);
    setView("verifying");
  }

  function cancelVerifying() {
    setVerifyAction(null);
    setVerifyCode("");
    setVerifyPassword("");
    setVerifyError(null);
    setView("on");
  }

  async function handleVerify(e: FormEvent) {
    e.preventDefault();
    if (!verifyAction) return;
    setVerifyError(null);
    if (
      verifyAction === "disable" &&
      hasPassword &&
      verifyPassword.trim() === ""
    ) {
      setVerifyError(t("passwordRequired"));
      return;
    }
    setVerifySubmitting(true);
    try {
      if (verifyAction === "disable") {
        const res = await disableTwoFactor(
          verifyCode,
          hasPassword ? verifyPassword : undefined,
        );
        adoptSession(res.auth);
        setVerifyAction(null);
        setVerifyCode("");
        setView("off");
      } else {
        const res = await regenerateRecoveryCodes(verifyCode);
        setRecoveryCodes(res.recoveryCodes);
        setCopiedCodes(false);
        setVerifyAction(null);
        setVerifyCode("");
        setView("codes");
      }
    } catch (err) {
      setVerifyError(err instanceof ApiError ? err.message : t("genericError"));
    } finally {
      setVerifySubmitting(false);
    }
  }

  async function copyText(text: string, onDone: (v: boolean) => void) {
    try {
      await navigator.clipboard.writeText(text);
      onDone(true);
      setTimeout(() => onDone(false), 2000);
    } catch {
      // Clipboard access can fail (permissions, insecure context): the key/codes are still
      // select-all text on screen, so this is not a dead end, just a missed shortcut.
    }
  }

  return (
    <div>
      {view === "loading" && (
        <SkeletonStatus label={t("checking")} count={2} />
      )}

      {view === "off" && (
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="text-sm text-white">{t("sectionTitle")}</div>
            <div className="text-sm text-muted mt-0.5">
              {t("offDescription")}
            </div>
            {enrolError && (
              <div role="alert" className="mt-2 error-banner">
                {enrolError}
              </div>
            )}
          </div>
          <button
            onClick={handleEnable}
            disabled={enrolSubmitting}
            className="btn-secondary shrink-0 gap-1.5 px-3 py-1.5 text-sm"
          >
            <IconShieldLock size={14} stroke={2} />
            {enrolSubmitting ? t("starting") : t("enable")}
          </button>
        </div>
      )}

      {view === "on" && (
        <div className="flex items-center justify-between gap-4 flex-wrap">
          <div>
            <div className="text-sm text-white">{t("sectionTitle")}</div>
            <div className="text-sm text-muted mt-0.5">
              {t("recoveryCodesRemaining", {
                count: recoveryCodesRemaining,
              })}
            </div>
          </div>
          <div className="flex items-center gap-3 flex-wrap">
            <button
              onClick={() => startVerifying("regenerate")}
              className="btn-secondary shrink-0 gap-1.5 px-3 py-1.5 text-sm"
            >
              <IconKey size={14} stroke={2} />
              {t("regenerateCodes")}
            </button>
            <button
              onClick={() => startVerifying("disable")}
              className="btn-danger shrink-0 gap-1.5 px-3 py-1.5 text-sm"
            >
              <IconShieldLock size={14} stroke={2} />
              {t("disableCaps")}
            </button>
          </div>
        </div>
      )}

      {view === "enrolling" && enrolment && (
        <form onSubmit={handleConfirm} className="flex flex-col gap-4">
          <p className="text-sm text-[var(--foreground)] leading-relaxed">
            {t("enrollIntro")}
          </p>

          {qrCodeSvg && (
            <div>
              <div className="text-sm font-medium text-[var(--foreground)] mb-1.5">
                {t("scanQrCode")}
              </div>
              <div
                role="img"
                aria-label={t("qrCodeAlt")}
                className="inline-block w-fit bg-white p-3"
                // qrcode's SVG output is generated locally from otpauthUri (no third-party
                // service ever sees the secret) and contains no user-controlled input.
                dangerouslySetInnerHTML={{ __html: qrCodeSvg }}
              />
            </div>
          )}

          <div>
            <div className="text-sm font-medium text-[var(--foreground)] mb-1.5">
              {t("setupKey")}
            </div>
            <div className="flex items-center gap-3">
              <code className="font-mono text-sm text-[var(--bounty)] select-all break-all">
                {groupKey(enrolment.sharedKey)}
              </code>
              <button
                type="button"
                aria-label={t("copySetupKeyAriaLabel")}
                onClick={() => copyText(enrolment.sharedKey, setCopiedKey)}
                className="btn-secondary shrink-0 p-2"
              >
                <IconCopy size={16} stroke={2} />
              </button>
            </div>
            {copiedKey && (
              <span
                role="status"
                className="text-sm font-semibold text-white mt-1 block"
              >
                {t("copiedPeriod")}
              </span>
            )}
          </div>

          <div>
            <a
              href={enrolment.otpauthUri}
              className="text-sm text-[var(--bounty)] font-medium hover:underline"
            >
              {t("openInAuthenticator")}
            </a>
            <code className="mt-1 block text-sm text-[var(--bounty)] select-all font-mono break-all">
              {enrolment.otpauthUri}
            </code>
            <p className="text-sm text-muted mt-1">{t("setupKeyFallback")}</p>
          </div>

          <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
            {t("sixDigitCode")}
            <input
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={11}
              value={enrolCode}
              onChange={(e) => setEnrolCode(e.target.value)}
              className={INPUT_CLASS}
              required
            />
          </label>

          {hasPassword && (
            <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
              {t("passwordLabel")}
              <input
                type="password"
                autoComplete="current-password"
                value={enrolPassword}
                onChange={(e) => setEnrolPassword(e.target.value)}
                className={INPUT_CLASS}
              />
            </label>
          )}

          {enrolError && (
            <div role="alert" className="error-banner">
              {enrolError}
            </div>
          )}

          <div className="flex items-center gap-3 flex-wrap">
            {hasPassword || googleReauthCode ? (
              <button
                type="submit"
                disabled={
                  enrolSubmitting ||
                  enrolCode.trim() === "" ||
                  (hasPassword === true && enrolPassword.trim() === "")
                }
                className="btn-primary flex-1 py-2.5 text-sm"
              >
                {enrolSubmitting ? t("confirmingEllipsis") : t("confirmCaps")}
              </button>
            ) : (
              <button
                type="button"
                onClick={startGoogleReauth}
                disabled={enrolSubmitting}
                className="btn-primary flex-1 py-2.5 text-sm flex items-center justify-center gap-2"
              >
                <IconBrandGoogle size={16} stroke={2} />
                {t("confirmWithGoogle")}
              </button>
            )}
            <button
              type="button"
              onClick={handleCancelEnrolment}
              disabled={enrolSubmitting}
              className="btn-secondary flex-1 py-2.5 text-sm"
            >
              {t("cancelCaps")}
            </button>
          </div>
        </form>
      )}

      {view === "codes" && (
        <div className="flex flex-col gap-4">
          <p className="text-sm text-loss">{t("saveCodesNow")}</p>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {recoveryCodes.map((c) => (
              <code
                key={c}
                className="select-all font-mono text-sm text-[var(--bounty)]"
              >
                {c}
              </code>
            ))}
          </div>

          <div className="flex items-center gap-3 flex-wrap">
            <button
              type="button"
              onClick={() => copyText(recoveryCodes.join("\n"), setCopiedCodes)}
              className="btn-secondary flex flex-1 gap-1.5 py-2.5 text-sm"
            >
              <IconCopy size={14} stroke={2} />
              {copiedCodes ? t("copied") : t("copyAll")}
            </button>
            <button
              type="button"
              onClick={handleDismissCodes}
              className="btn-primary flex-1 py-2.5 text-sm"
            >
              {t("haveSavedThese")}
            </button>
          </div>
        </div>
      )}

      {view === "verifying" && verifyAction && (
        <form onSubmit={handleVerify} className="flex flex-col gap-4">
          <p className="text-sm text-loss">
            {verifyAction === "disable"
              ? t("disableWarning")
              : t("regenerateWarning")}
          </p>

          <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
            {t("enterCodeToConfirm")}
            <input
              inputMode="numeric"
              autoComplete="one-time-code"
              autoFocus
              maxLength={11}
              value={verifyCode}
              onChange={(e) => setVerifyCode(e.target.value)}
              className={INPUT_CLASS}
              required
            />
          </label>

          {verifyAction === "disable" && hasPassword && (
            <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
              {t("passwordLabel")}
              <input
                type="password"
                autoComplete="current-password"
                value={verifyPassword}
                onChange={(e) => setVerifyPassword(e.target.value)}
                className={INPUT_CLASS}
              />
            </label>
          )}

          {verifyError && (
            <div role="alert" className="error-banner">
              {verifyError}
            </div>
          )}

          <div className="flex items-center gap-3 flex-wrap">
            <button
              type="submit"
              disabled={
                verifySubmitting ||
                verifyCode.trim() === "" ||
                (verifyAction === "disable" &&
                  hasPassword === true &&
                  verifyPassword.trim() === "")
              }
              className={`flex-1 py-2.5 text-sm ${
                verifyAction === "disable" ? "btn-danger-fill" : "btn-primary"
              }`}
            >
              {verifySubmitting
                ? t("confirmingEllipsis")
                : verifyAction === "disable"
                  ? t("confirmDisable")
                  : t("confirmRegenerate")}
            </button>
            <button
              type="button"
              onClick={cancelVerifying}
              disabled={verifySubmitting}
              className="btn-secondary flex-1 py-2.5 text-sm"
            >
              {t("cancel")}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}
