"use client";

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { type PatronTierName } from "@/components/PatronIcon";
import { IconHeart } from "@tabler/icons-react";
import { appendReference } from "@/lib/donationLink";
import { createDonationReference } from "@/lib/api";

type Billing = "once" | "monthly";

interface Tier {
  key: string;
  /** API grant tier this amount maps to. Not shown; kept so the donate table and
   *  `PATRON_TIERS` cannot drift apart. */
  tierName: PatronTierName;
  amount: number;
  stripeOnce: string;
}

// Customer-chooses-price link: the amount is entered on Stripe's own page, since
// payment links take no amount URL parameter (only prefilled_email, prefilled_promo_code,
// locked_prefilled_email, locale and client_reference_id).
export const STRIPE_CUSTOM_AMOUNT =
  "https://buy.stripe.com/8x2aEW685gEd56hgL6dby0a";

/** Suggested amount, pre-selected the way a donor lands on the picker. */
const DEFAULT_AMOUNT_KEY = "knight";

const CUSTOM_AMOUNT = "custom";

/**
 * Recurring donations are off, and this is a legal decision rather than a UI one. Do not flip it
 * back without reading `docs/ROADMAP.md` FS.4 and `docs/audits/L.md` L-7-1 first.
 *
 * The short version: Belgium's e-commerce transparency duties (Book XII WER) are scoped to services
 * "normally provided for remuneration". A free site funded by one-off gifts sits outside that; a
 * recurring paid tier that delivers a visible benefit is the case those rules are written about, and
 * the consequence is publishing a geographic address. The owner has no address to publish that is
 * not a private home, so the tiers come off instead. One-off donations, and the patron badge that
 * follows from them, are unaffected.
 *
 * Turning this back to `true` is NOT sufficient on its own: monthly payment links must be created
 * (or reactivated) in the Stripe dashboard AND added back to this file, and TERMS section 12 says
 * one-off only while this is false. The monthly URLs used to live here; they were stripped on
 * 2026-09-04 ($2-3) after the owner deactivated them in Stripe, so they no longer ship in the
 * public JS bundle.
 */
export const MONTHLY_ENABLED = false;

export const TIERS: Tier[] = [
  {
    key: "pawn",
    tierName: "Pawn",
    amount: 5,
    stripeOnce: "https://buy.stripe.com/fZueVc7c93Rr56h1Qcdby00",
  },
  {
    key: "knight",
    tierName: "Knight",
    amount: 10,
    stripeOnce: "https://buy.stripe.com/8x200i2VT4Vv8it0M8dby01",
  },
  {
    key: "bishop",
    tierName: "Bishop",
    amount: 15,
    stripeOnce: "https://buy.stripe.com/8x23cufIF2NngOZcuQdby02",
  },
  {
    key: "rook",
    tierName: "Rook",
    amount: 25,
    stripeOnce: "https://buy.stripe.com/6oU5kC9khfA9eGR0M8dby03",
  },
  {
    key: "queen",
    tierName: "Queen",
    amount: 50,
    stripeOnce: "https://buy.stripe.com/aFa3cu8gdbjT9mxdyUdby04",
  },
];

/** Tab-chip size from the button scale, stretched across its grid cell. */
function choiceClass(selected: boolean) {
  return `w-full py-2.5 text-sm whitespace-nowrap ${selected ? "btn-primary" : "btn-secondary"}`;
}

/**
 * Collapses remounts (React Strict Mode, navigating away and back) so a signed-in donor does not
 * re-mint while a reference is already in flight or already held ($1-4). Failed mints drop the
 * entry so a later visit can try again.
 */
const mintByUser = new Map<string, Promise<string>>();

export function DonationTiers() {
  const t = useTranslations("DonationTiers");
  const [billing, setBilling] = useState<Billing>("once");
  const [amountKey, setAmountKey] = useState(DEFAULT_AMOUNT_KEY);
  const optionRefs = useRef<Record<string, HTMLButtonElement | null>>({});
  const { userId, hydrated } = useAuth();
  const [reference, setReference] = useState<string | null>(null);
  const [mintFailed, setMintFailed] = useState(false);

  /**
   * Mint the reference once a signed-in visitor lands here, so every link below can carry it as
   * `client_reference_id`. Stripe never sees the account id itself, only this single-use row.
   * A donation must never be blocked by this: a failed fetch, a
   * signed-out visitor, or the request simply not having landed yet all leave `reference` null, and
   * every link below (`appendReference`) falls back to the plain payment link. A failed mint is no
   * longer silent ($1-4): the warning below tells the donor to sign in again rather than pay blind.
   */
  useEffect(() => {
    if (!hydrated) return;
    if (!userId) {
      setReference(null);
      setMintFailed(false);
      return;
    }

    let cancelled = false;
    setReference(null);
    setMintFailed(false);
    let pending = mintByUser.get(userId);
    if (!pending) {
      pending = createDonationReference().then((res) => res.reference);
      mintByUser.set(userId, pending);
      pending.catch(() => {
        mintByUser.delete(userId);
      });
    }
    pending
      .then((ref) => {
        if (!cancelled) setReference(ref);
      })
      .catch(() => {
        if (!cancelled) setMintFailed(true);
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, userId]);

  const amountChoices = [...TIERS.map((tier) => tier.key), CUSTOM_AMOUNT];
  const selectedTier = TIERS.find((tier) => tier.key === amountKey);
  const donateHref = appendReference(
    selectedTier?.stripeOnce ?? STRIPE_CUSTOM_AMOUNT,
    reference,
  );
  // Signed-out donors used to reach Stripe with no client_reference_id and then had no path to a
  // badge ($1-8). Gate the payment link until AuthProvider has resolved and a session is present.
  const canDonate = hydrated && Boolean(userId);

  function onAmountKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = amountChoices.indexOf(amountKey);
    let next = index;
    switch (event.key) {
      case "ArrowRight":
      case "ArrowDown":
        next = (index + 1) % amountChoices.length;
        break;
      case "ArrowLeft":
      case "ArrowUp":
        next = (index - 1 + amountChoices.length) % amountChoices.length;
        break;
      case "Home":
        next = 0;
        break;
      case "End":
        next = amountChoices.length - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const key = amountChoices[next];
    setAmountKey(key);
    optionRefs.current[key]?.focus();
  }

  return (
    <div className="mx-auto flex w-full max-w-2xl flex-col items-center gap-8">
      <IconHeart
        size={64}
        stroke={2}
        aria-hidden
        className="text-[var(--bounty)]"
      />

      <div className="flex w-full flex-col gap-3">
        {/* One quiet line, not a banner and not a modal. `hydrated` gates this so a signed-in visitor
            never sees it flash before AuthProvider finishes reading localStorage. */}
        {hydrated && !userId && (
          <p className="text-center text-sm text-muted">
            {t.rich("signInHint", {
              login: (chunks) => (
                <Link href="/login" className="text-[var(--bounty)] underline">
                  {chunks}
                </Link>
              ),
            })}
          </p>
        )}

        {hydrated && userId && mintFailed && (
          <p role="alert" className="error-banner">
            {t("referenceMintFailed")}
          </p>
        )}

        <p className="text-center text-sm text-[var(--foreground)] leading-relaxed">
          {t("badgeGrant")}
        </p>

        {/* Monthly is off (see MONTHLY_ENABLED), so there is no frequency control on the page.
            This sentence is the reason, and it has to be readable without a dimmed button. */}
        {!MONTHLY_ENABLED && (
          <p
            id="monthly-unavailable-note"
            className="text-center text-sm text-muted"
          >
            {t("monthlyUnavailable")}
          </p>
        )}
      </div>

      <div className="flex w-full flex-col gap-6">
        {/* Chips with a gap, the same control as a filter row. The frequency pair renders only
            once monthly links exist again; until then the note above is the whole explanation. */}
        {MONTHLY_ENABLED && (
          <div
            role="group"
            aria-label={t("donationFrequencyAriaLabel")}
            className="grid grid-cols-2 gap-3"
          >
            <button
              type="button"
              aria-pressed={billing === "once"}
              onClick={() => setBilling("once")}
              className={choiceClass(billing === "once")}
            >
              {t("oneTime")}
            </button>
            <button
              type="button"
              aria-pressed={billing === "monthly"}
              onClick={() => setBilling("monthly")}
              className={choiceClass(billing === "monthly")}
            >
              {t("monthly")}
            </button>
          </div>
        )}

        <div
          role="radiogroup"
          aria-label={t("amountGroupLabel")}
          onKeyDown={onAmountKeyDown}
          className="flex flex-col gap-3"
        >
          <div className="grid grid-cols-5 gap-3">
            {TIERS.map((tier) => {
              const selected = amountKey === tier.key;
              return (
                <button
                  key={tier.key}
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  tabIndex={selected ? 0 : -1}
                  ref={(node) => {
                    optionRefs.current[tier.key] = node;
                  }}
                  onClick={() => setAmountKey(tier.key)}
                  className={choiceClass(selected)}
                >
                  {t("amount", { amount: tier.amount })}
                </button>
              );
            })}
          </div>
          <button
            type="button"
            role="radio"
            aria-checked={amountKey === CUSTOM_AMOUNT}
            tabIndex={amountKey === CUSTOM_AMOUNT ? 0 : -1}
            ref={(node) => {
              optionRefs.current[CUSTOM_AMOUNT] = node;
            }}
            onClick={() => setAmountKey(CUSTOM_AMOUNT)}
            className={choiceClass(amountKey === CUSTOM_AMOUNT)}
          >
            {t("other")}
          </button>
        </div>

        {amountKey === CUSTOM_AMOUNT && (
          <p className="text-center text-sm text-muted leading-relaxed">
            {t.rich("everyBitHelps", {
              minimum: (chunks) => <span className="text-muted">{chunks}</span>,
            })}
          </p>
        )}

        {canDonate ? (
          <a
            href={donateHref}
            target="_blank"
            rel="noopener noreferrer"
            className="btn-primary w-full py-2.5 text-sm"
          >
            {t("donate")}
          </a>
        ) : (
          <button
            type="button"
            disabled
            className="btn-primary w-full py-2.5 text-sm opacity-50"
          >
            {t("donate")}
          </button>
        )}
      </div>
    </div>
  );
}

/** Vitest only: the module-level mint map outlives a remount, which is the point in the browser
 *  and would otherwise leak one test's reference into the next. */
export function resetDonationMintCacheForTests() {
  mintByUser.clear();
}

