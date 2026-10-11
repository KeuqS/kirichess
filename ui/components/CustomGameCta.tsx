"use client";

import { Link } from "@/i18n/navigation";
import { useAuth } from "@/components/AuthProvider";

const CTA_CLASS = "btn-primary px-5 py-2 text-sm whitespace-nowrap";

export function CustomGameCta({
  buildLabel,
  signInLabel,
}: {
  buildLabel: string;
  signInLabel: string;
}) {
  const { token, hydrated } = useAuth();

  if (!hydrated) {
    return (
      <span className={`${CTA_CLASS} invisible`} aria-hidden>
        {signInLabel}
      </span>
    );
  }

  if (!token) {
    return (
      <Link
        href={`/login?next=${encodeURIComponent("/custom")}`}
        className={CTA_CLASS}
      >
        {signInLabel}
      </Link>
    );
  }

  return (
    <Link href="/custom" className={CTA_CLASS}>
      {buildLabel}
    </Link>
  );
}
