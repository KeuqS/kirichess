"use client";

import { useEffect, useRef } from "react";
import { usePathname } from "@/i18n/navigation";
import { recordCounter } from "@/lib/api";
import { PAGE_KEYS } from "@/lib/analytics";

/**
 * AD.6 / 11a.3. Mounted once in the root layout. Fires one allowlisted page counter per navigation,
 * by exact pathname match against `PAGE_KEYS` only - never a prefix match, so `/game/<id>`,
 * `/replay/<id>`, `/profile`, `/moderation/*` and `/challenge/<token>` are never reported. A route
 * missing from the map (any dynamic or unlisted page) simply fires nothing.
 *
 * Renders null: no wrapper element, no layout impact.
 */
export function CounterBeacon() {
  const pathname = usePathname();
  const lastSent = useRef<string | null>(null);

  useEffect(() => {
    const key = PAGE_KEYS[pathname];
    if (!key || lastSent.current === key) return;
    lastSent.current = key;
    recordCounter(key);
  }, [pathname]);

  return null;
}
