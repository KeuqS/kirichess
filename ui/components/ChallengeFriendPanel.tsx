"use client";

import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type ReactNode,
} from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconCheck, IconCopy, IconX } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { Card } from "@/components/Card";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { useGameHub } from "@/components/GameHubProvider";
import {
  ApiError,
  cancelChallenge,
  createChallenge,
  getMyChallenges,
} from "@/lib/api";
import {
  isPresetKey,
  PRESETS,
  PRESET_ORDER,
  type PresetKey,
} from "@/lib/presets";
import { SITE_URL } from "@/lib/site";
import type { ChallengeColour, ChallengeDto } from "@/lib/types";

const COLOUR_OPTIONS: ChallengeColour[] = ["Random", "White", "Black"];

/**
 * Phase 9d.8. The lobby half of the direct-challenge feature: create a link, watch it live, hand it
 * to a friend. Self-contained and imported by nothing (Phase 9d.8's
 * first hard boundary), a parallel session is rewriting ui/app/page.tsx around RatedPoolsGrid /
 * BotPlayPanel and this worktree's copy of it is stale, so the integrator wires the one import in
 * by hand rather than this task touching that file.
 */
export function ChallengeFriendPanel() {
  const t = useTranslations("ChallengeFriendPanel");
  const { token, hydrated } = useAuth();
  const { ensure, onReconnect } = useGameHub();
  const colourOptionLabel: Record<ChallengeColour, string> = {
    Random: t("colourRandom"),
    White: t("colourWhite"),
    Black: t("colourBlack"),
  };

  const [pool, setPool] = useState<PresetKey>(PRESET_ORDER[0]);
  const [colour, setColour] = useState<ChallengeColour>("Random");

  const [challenge, setChallenge] = useState<ChallengeDto | null>(null);
  const [loadingMine, setLoadingMine] = useState(true);
  const [creating, setCreating] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [copied, setCopied] = useState(false);
  const [clipboardFailed, setClipboardFailed] = useState(false);
  const copyTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const linkInputRef = useRef<HTMLInputElement>(null);

  // Drives the "expires in N minutes" readout below, ticked coarsely (30s) rather than every
  // second, nobody needs a live stopwatch for a 60-minute link, and a coarser tick is one less
  // thing re-rendering the panel while the friend has not shown up yet.
  const [nowTick, setNowTick] = useState(() => Date.now());

  // Restores an open challenge across a reload, the entire reason scope decision 1 keeps the token
  // in plaintext instead of hashing it like the other three token tables: a hash can't be
  // re-displayed, and this effect is what re-displays it.
  useEffect(() => {
    if (!hydrated) return;
    if (!token) {
      setLoadingMine(false);
      return;
    }
    let cancelled = false;
    getMyChallenges()
      .then((mine) => {
        if (cancelled) return;
        // Custom-config invites belong on /custom, not here: restoring one would show the
        // sentinel pool key as if it were a rated tag.
        const open = mine.find(
          (c) => c.status === "Open" && isPresetKey(c.pool),
        );
        if (open) setChallenge(open);
      })
      .catch(() => {
        // Nothing to restore is not worth surfacing on mount, the create form below still works.
      })
      .finally(() => {
        if (!cancelled) setLoadingMine(false);
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, token]);

  useEffect(() => {
    const id = setInterval(() => setNowTick(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);

  useEffect(() => {
    return () => {
      if (copyTimeoutRef.current) clearTimeout(copyTimeoutRef.current);
    };
  }, []);

  const expiresInMinutes = challenge
    ? Math.max(
        0,
        Math.ceil((new Date(challenge.expiresAt).getTime() - nowTick) / 60000),
      )
    : 0;
  const localExpired = challenge !== null && expiresInMinutes <= 0;

  // The server is the authority on expiry, not this clock, but once the local countdown hits
  // zero there is nothing useful left to show, so drop back to the create form rather than leave
  // "Expires in 0 minutes" on screen forever. The watch effect below tears down the connection
  // the moment `challenge` clears.
  useEffect(() => {
    if (localExpired) setChallenge(null);
  }, [localExpired]);

  // Scope decision 4: a SignalR *group* keyed by token, not a user id (Clients.User(...) is
  // unreliable here, README fact 4). Runs whenever there is an Open challenge to watch, whether
  // just created below or restored on mount above, and re-joins the group on every reconnect
  // because a reconnect drops group membership and this re-join is the entire fallback story ,
  // there is no polling timer (scope decision 4).
  useEffect(() => {
    if (!challenge || challenge.status !== "Open") return;
    const watchToken = challenge.token;

    async function watch() {
      try {
        const conn = await ensure();
        await conn.invoke("WatchChallenge", watchToken);
      } catch {
        // Best-effort: a failed join here is retried by the reconnect handler below, or by a
        // plain reload, the restore effect above is the ultimate fallback.
      }
    }

    const unsubscribe = onReconnect(() => {
      void watch();
    });
    void watch();

    return () => {
      unsubscribe();
      void ensure()
        .then((conn) => conn.invoke("UnwatchChallenge", watchToken))
        .catch(() => {
          // Shared hub stays up for the rest of the lobby; Unwatch is courtesy.
        });
    };
  }, [challenge?.token, challenge?.status, ensure, onReconnect]);

  async function handleCreate(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setCreating(true);
    try {
      const created = await createChallenge(pool, colour);
      setChallenge(created);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("failedToCreate"));
    } finally {
      setCreating(false);
    }
  }

  async function handleCancel() {
    if (!challenge) return;
    setCancelling(true);
    setError(null);
    try {
      await cancelChallenge(challenge.token);
      setChallenge(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("failedToCancel"));
    } finally {
      setCancelling(false);
    }
  }

  function markCopied() {
    setClipboardFailed(false);
    setCopied(true);
    if (copyTimeoutRef.current) clearTimeout(copyTimeoutRef.current);
    copyTimeoutRef.current = setTimeout(() => setCopied(false), 2000);
  }

  function copyFromInput(): boolean {
    const input = linkInputRef.current;
    if (!input) return false;
    input.focus();
    input.select();
    try {
      return document.execCommand("copy");
    } catch {
      return false;
    }
  }

  // Not async: a thenable returned from onClick is a render error in React 19, which Next.js
  // surfaces as error.tsx ("Something went wrong") instead of the clipboard-failed fallback.
  function handleCopy(url: string) {
    const writeText = navigator.clipboard?.writeText;
    if (writeText) {
      void writeText.call(navigator.clipboard, url).then(markCopied, () => {
        if (copyFromInput()) markCopied();
        else setClipboardFailed(true);
      });
      return;
    }
    if (copyFromInput()) markCopied();
    else setClipboardFailed(true);
  }

  let body: ReactNode = null;
  let heading = t("playAFriend");

  if (!hydrated || loadingMine) {
    body = <SkeletonStatus label={t("loadingEllipsis")} count={3} />;
  } else if (token && challenge && !localExpired) {
    heading = t("waitingForFriend");
    const shareUrl = `${SITE_URL}/challenge/${challenge.token}`;
    const poolLabel = isPresetKey(challenge.pool)
      ? PRESETS[challenge.pool].tag
      : challenge.pool;
    const colourLabel =
      challenge.colour === "Random"
        ? t("randomColour")
        : t("youPlay", { side: challenge.colour });

    body = (
      <>
        <p className="text-sm text-muted">
          {poolLabel} - {t("unrated")} - {colourLabel}
        </p>

        <div className="flex items-center gap-2">
          <input
            ref={linkInputRef}
            readOnly
            value={shareUrl}
            aria-label={t("challengeLinkAriaLabel")}
            onFocus={(e) => e.currentTarget.select()}
            className="min-w-0 flex-1 truncate border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white"
          />
          <button
            type="button"
            onClick={() => handleCopy(shareUrl)}
            className="btn-secondary flex shrink-0 gap-1.5 px-3 py-2 text-sm"
          >
            {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
            {copied ? t("copied") : t("copy")}
          </button>
        </div>
        {/* Announces the copy confirmation for screen-reader users, who cannot see the button's
            label swap above. */}
        <span role="status" aria-live="polite" className="sr-only">
          {copied ? t("copiedAnnouncement") : ""}
        </span>
        {clipboardFailed && (
          <p className="text-sm text-muted">{t("clipboardFailed")}</p>
        )}

        <p className="text-sm text-muted">
          {t("expiresIn", { minutes: expiresInMinutes })}
        </p>

        {error && <p className="text-sm text-loss">{error}</p>}

        <button
          type="button"
          onClick={handleCancel}
          disabled={cancelling}
          className="btn-danger flex gap-1.5 self-start px-3 py-1.5 text-sm"
        >
          <IconX size={14} />
          {cancelling ? t("cancelling") : t("cancelChallenge")}
        </button>
      </>
    );
  } else if (token) {
    body = (
      <form onSubmit={handleCreate} className="flex flex-col gap-4">
        <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
          {t("poolLabel")}
          <select
            value={pool}
            onChange={(e) => setPool(e.target.value as PresetKey)}
            className="input-select border border-[var(--border)] bg-[var(--background)] px-3 py-2 pr-10 text-sm text-white focus:border-[var(--bounty)] focus:ring-1 focus:ring-[var(--bounty)] focus:outline-none"
          >
            {PRESET_ORDER.map((key) => (
              <option key={key} value={key}>
                {PRESETS[key].label} - {PRESETS[key].tag}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
          {t("yourColourLabel")}
          <select
            value={colour}
            onChange={(e) => setColour(e.target.value as ChallengeColour)}
            className="input-select border border-[var(--border)] bg-[var(--background)] px-3 py-2 pr-10 text-sm text-white focus:border-[var(--bounty)] focus:ring-1 focus:ring-[var(--bounty)] focus:outline-none"
          >
            {COLOUR_OPTIONS.map((value) => (
              <option key={value} value={value}>
                {colourOptionLabel[value]}
              </option>
            ))}
          </select>
        </label>

        {error && <p className="text-sm text-loss">{error}</p>}

        <button
          type="submit"
          disabled={creating}
          className="btn-primary mt-1 py-2.5"
        >
          {creating ? t("creating") : t("createLink")}
        </button>
      </form>
    );
  }

  const guestCta = hydrated && !token && !loadingMine;

  return (
    <Card className="flex flex-col gap-4">
      <div
        className={
          guestCta
            ? "flex items-center justify-between gap-6 flex-wrap"
            : undefined
        }
      >
        <div className={guestCta ? "max-w-xl" : undefined}>
          <h2 className="text-lg font-bold text-white">{heading}</h2>
        </div>
        {guestCta ? (
          <Link
            href={`/login?next=${encodeURIComponent("/")}`}
            className="btn-primary px-5 py-2 text-sm whitespace-nowrap"
          >
            {t("signInToChallenge")}
          </Link>
        ) : null}
      </div>
      {body}
    </Card>
  );
}
