"use client";

import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";
import { useAuth } from "@/components/AuthProvider";
import { TwoFactorCard } from "@/components/TwoFactorCard";
import { SessionsCard } from "@/components/SessionsCard";

/**
 * Profile security card: 2FA enrolment and active sessions in one surface. Category title sits
 * above the card, same placement as Rated pools / Match history / the other live cards on
 * `/profile`. Renders nothing when signed out.
 */
export function SecurityCard() {
  const t = useTranslations("SecurityCard");
  const { token } = useAuth();

  if (!token) return null;

  return (
    <div className="mb-8">
      <h2 className="profile-category mb-4">
        {t("heading")}
      </h2>
      <Card>
        <TwoFactorCard />
        <SessionsCard />
      </Card>
    </div>
  );
}
