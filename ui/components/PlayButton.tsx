"use client";

import { useState } from "react";
import { Link, usePathname } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { BotGameDialog } from "@/components/BotGameDialog";
import { loginNextHref } from "@/lib/challengeLink";
import type { PresetKey } from "@/lib/presets";

export function PlayButton({
  preset,
  children,
  style,
  className,
}: {
  preset: PresetKey;
  children: React.ReactNode;
  style?: React.CSSProperties;
  className?: string;
}) {
  const t = useTranslations("PlayButton");
  const pathname = usePathname();
  const { token } = useAuth();
  const [dialogOpen, setDialogOpen] = useState(false);

  if (!token) {
    return (
      <Link href={loginNextHref(pathname)} style={style} className={className}>
        {t("signInToPlay")}
      </Link>
    );
  }

  return (
    <>
      <button
        type="button"
        onClick={() => setDialogOpen(true)}
        style={style}
        className={className}
      >
        {children}
      </button>
      {dialogOpen && (
        <BotGameDialog
          presetKey={preset}
          onClose={() => setDialogOpen(false)}
        />
      )}
    </>
  );
}
