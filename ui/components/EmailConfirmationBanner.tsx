"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { IconMailExclamation } from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { useAuth } from "@/components/AuthProvider";
import { emailStatus, resendConfirmation } from "@/lib/api";

type SendState = "idle" | "sending" | "sent" | "failed";

/**
 * Shown to a signed-in player whose address is unconfirmed *and* who is actually walled by the
 * confirmed-email play gate. Both halves matter: `playBlocked` is the server's verdict, and an
 * unconfirmed account that predates the cutoff is grandfathered and must not be nagged about a link
 * it never needed.
 *
 * This exists because the gate is otherwise a dead end. `/api/auth/resend-confirmation` is what
 * `docs/ops/DEPLOYMENT.md` calls the recovery path for a mail queue it also describes as in-process
 * and not durable - a restart drops whatever is queued - and until this component there was no way
 * to reach it from the app at all. A mistyped address or a spam-foldered mail would have been
 * unrecoverable for a player who can no longer play.
 *
 * Fetch failures render nothing, silently, the contract `SanctionBanner` sets: neither the lobby nor
 * the profile may break because this call failed. Resend always reports success, matching the
 * endpoint, which answers `{ sent: true }` for an unknown or already-confirmed address on purpose so
 * it cannot become a confirmation-status oracle.
 */
export function EmailConfirmationBanner() {
  const t = useTranslations("EmailConfirmationBanner");
  const { token, hydrated } = useAuth();
  const [blocked, setBlocked] = useState(false);
  const [email, setEmail] = useState<string | null>(null);
  const [send, setSend] = useState<SendState>("idle");

  useEffect(() => {
    if (!hydrated || !token) return;

    let cancelled = false;
    emailStatus()
      .then((status) => {
        if (cancelled) return;
        setBlocked(status.playBlocked);
        setEmail(status.email);
      })
      .catch(() => {
        // Silent: see doc comment above.
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, token]);

  if (!blocked) return null;

  async function resend() {
    if (!email) return;
    setSend("sending");
    try {
      await resendConfirmation(email);
      setSend("sent");
    } catch {
      setSend("failed");
    }
  }

  return (
    <Card className="flex items-start gap-3 mb-8">
      <IconMailExclamation
        size={20}
        stroke={2}
        className="mt-0.5 shrink-0 text-muted"
      />
      <div className="flex flex-col gap-2">
        <div className="text-sm font-bold text-white">
          {t("heading")}
        </div>
        <p className="text-sm text-[var(--foreground)] leading-relaxed m-0">
          {email ? t("bodyWithAddress", { email }) : t("body")}
        </p>

        {send === "sent" ? (
          <span className="text-sm text-muted">{t("sent")}</span>
        ) : (
          <div className="flex items-center gap-3 flex-wrap">
            <button
              type="button"
              onClick={resend}
              disabled={send === "sending" || !email}
              className="btn-secondary px-3 py-1.5 text-sm"
            >
              {send === "sending" ? t("sending") : t("resend")}
            </button>
            {send === "failed" && (
              <span className="text-sm text-[var(--loss)]">{t("failed")}</span>
            )}
          </div>
        )}
      </div>
    </Card>
  );
}
