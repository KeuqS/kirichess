"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { IconDownload } from "@tabler/icons-react";

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

/**
 * Chrome/Edge/Android fire `beforeinstallprompt` only when their own install heuristics are met
 * (already visited, manifest + service worker present) and only once until the page reloads, so
 * this renders nothing until the browser actually offers it. Safari/iOS never fire this event at
 * all (install there is the OS-level "Add to Home Screen" share-sheet action, nothing a page can
 * trigger), so this button simply never appears there rather than showing a broken one.
 */
export function InstallAppButton({ className }: { className?: string }) {
  const t = useTranslations("InstallAppButton");
  const [installEvent, setInstallEvent] =
    useState<BeforeInstallPromptEvent | null>(null);

  useEffect(() => {
    const onBeforeInstallPrompt = (event: Event) => {
      event.preventDefault();
      setInstallEvent(event as BeforeInstallPromptEvent);
    };
    const onInstalled = () => setInstallEvent(null);

    window.addEventListener("beforeinstallprompt", onBeforeInstallPrompt);
    window.addEventListener("appinstalled", onInstalled);
    return () => {
      window.removeEventListener(
        "beforeinstallprompt",
        onBeforeInstallPrompt,
      );
      window.removeEventListener("appinstalled", onInstalled);
    };
  }, []);

  if (!installEvent) return null;

  return (
    <button
      type="button"
      onClick={async () => {
        await installEvent.prompt();
        const { outcome } = await installEvent.userChoice;
        // Both outcomes consume the browser's one-shot prompt event; only a fresh
        // `beforeinstallprompt` (e.g. after a reload) can offer it again.
        if (outcome === "accepted" || outcome === "dismissed") {
          setInstallEvent(null);
        }
      }}
      className={
        className ??
        "btn-secondary flex gap-1.5 px-3 py-1.5 text-sm"
      }
    >
      <IconDownload size={16} stroke={2} />
      {t("installApp")}
    </button>
  );
}
