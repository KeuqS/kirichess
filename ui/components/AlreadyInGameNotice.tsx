"use client";

import { useEffect, useState } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { getCurrentGame } from "@/lib/api";
import {
  ALREADY_IN_GAME_MESSAGE,
  recallBotGame,
  returnToGameHref,
} from "@/lib/returnToGame";

/**
 * Renders a play-refusal. The one sentence the server uses for "you are already seated" becomes a
 * link back to that board; every other message is shown as the server sent it.
 */
export function AlreadyInGameNotice({
  message,
  className,
}: {
  message: string;
  className?: string;
}) {
  const t = useTranslations("play");
  const resume = message === ALREADY_IN_GAME_MESSAGE;
  const [href, setHref] = useState<string | null>(null);

  useEffect(() => {
    if (!resume) return;
    let cancelled = false;
    getCurrentGame()
      .then((game) => {
        if (cancelled || !game) return;
        setHref(returnToGameHref(game, recallBotGame()));
      })
      .catch(() => {
        // The sentence still stands. A failed lookup must not replace it with a second error.
      });
    return () => {
      cancelled = true;
    };
  }, [resume]);

  if (resume && href) {
    return (
      <p className={className}>
        {t.rich("alreadyInGame", {
          link: (chunks) => (
            <Link href={href} className="underline underline-offset-2">
              {chunks}
            </Link>
          ),
        })}
      </p>
    );
  }

  return (
    <p className={className}>{resume ? t("alreadyInGamePlain") : message}</p>
  );
}
