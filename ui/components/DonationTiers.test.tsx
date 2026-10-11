import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import {
  DonationTiers,
  MONTHLY_ENABLED,
  resetDonationMintCacheForTests,
  STRIPE_CUSTOM_AMOUNT,
  TIERS,
} from "./DonationTiers";
import { PATRON_TIERS } from "./PatronIcon";

const UI_ROOT = join(__dirname, "..");
const REPO_ROOT = join(UI_ROOT, "..");

/** The five monthly Stripe payment links deactivated 2026-09-04. They must not reappear in source. */
const DEACTIVATED_MONTHLY_URLS = [
  "https://buy.stripe.com/5kQcN4bspds1eGR66sdby05",
  "https://buy.stripe.com/aFaaEWgMJcnXgOZ7awdby06",
  "https://buy.stripe.com/00weVcdAxew50Q1gL6dby07",
  "https://buy.stripe.com/5kQfZgaol2Nn2Y91Qcdby08",
  "https://buy.stripe.com/aFa4gy9kh73D2Y91Qcdby09",
] as const;

describe("recurring donations", () => {
  /**
   * This is a legal position, not a styling choice, so it gets a test rather than only a comment.
   * Belgium's e-commerce transparency duties are scoped to services "normally provided for
   * remuneration"; a recurring tier that delivers a visible benefit lands inside that and the price
   * of it is publishing a geographic address the owner does not have. See ROADMAP FS.4 and
   * docs/audits/L.md L-7-1.
   *
   * If you are here because this test failed, you flipped MONTHLY_ENABLED. Re-read those two
   * documents first, and remember the flag alone does not re-enable anything: monthly payment
   * links must be reactivated in Stripe AND added back to this file, and TERMS section 12 currently
   * says one-off only.
   */
  it("stays off until the address question in FS.4 is answered", () => {
    expect(MONTHLY_ENABLED).toBe(false);
  });

  it("does not ship the deactivated monthly payment links ($2-3)", () => {
    const source = readFileSync(join(__dirname, "DonationTiers.tsx"), "utf8");
    for (const url of DEACTIVATED_MONTHLY_URLS) {
      expect(source, url).not.toContain(url);
    }
    expect(
      TIERS.some((tier) => "stripeMonthly" in tier),
      "TIERS still carries a stripeMonthly field",
    ).toBe(false);
  });

  it("agrees with TERMS §12 that donations are one-off only while the flag is off", () => {
    const terms = readFileSync(
      join(REPO_ROOT, "docs/legal/TERMS.md"),
      "utf8",
    );
    if (!MONTHLY_ENABLED) {
      expect(terms.replace(/\s+/g, " ")).toContain(
        "There is no subscription and no recurring donation",
      );
    }
  });
});

describe("DonationTiers tier table", () => {
  it("covers exactly the tiers PatronIcon knows about, so the donate amounts and the badge grant cannot drift apart", () => {
    const tierNames = TIERS.map((tier) => tier.tierName).sort();
    expect(tierNames).toEqual([...PATRON_TIERS].sort());
  });
});

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const api = vi.hoisted(() => ({
  createDonationReference: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  createDonationReference: api.createDonationReference,
}));

function renderTiers() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <DonationTiers />
    </NextIntlClientProvider>,
  );
}

function amountLabel(amount: number) {
  return en.DonationTiers.amount.replace("{amount}", String(amount));
}

describe("DonationTiers reference mint ($1-4)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    resetDonationMintCacheForTests();
  });

  afterEach(() => {
    resetDonationMintCacheForTests();
  });

  it("disables the Donate control until the visitor is signed in ($1-8)", () => {
    useAuth.mockReturnValue({ userId: null, hydrated: true });

    renderTiers();

    expect(screen.queryByRole("link", { name: "Donate" })).toBeNull();
    expect(screen.getByRole("button", { name: "Donate" })).toBeDisabled();
  });

  it("shows an error banner when the mint fails and still leaves the payment links clickable", async () => {
    useAuth.mockReturnValue({ userId: "donor-1", hydrated: true });
    api.createDonationReference.mockRejectedValue(new Error("mint failed"));

    renderTiers();

    const banner = await screen.findByRole("alert");
    expect(banner).toHaveClass("error-banner");
    expect(banner).toHaveTextContent(en.DonationTiers.referenceMintFailed);

    const donate = screen.getByRole("link", { name: "Donate" });
    expect(donate.getAttribute("href")).not.toContain("client_reference_id");

    for (const tier of TIERS) {
      fireEvent.click(screen.getByRole("radio", { name: amountLabel(tier.amount) }));
      expect(donate.getAttribute("href")).toBe(tier.stripeOnce);
    }
    fireEvent.click(screen.getByRole("radio", { name: en.DonationTiers.other }));
    expect(donate.getAttribute("href")).toBe(STRIPE_CUSTOM_AMOUNT);
  });

  it("does not remint when the same signed-in donor remounts", async () => {
    useAuth.mockReturnValue({ userId: "donor-2", hydrated: true });
    api.createDonationReference.mockResolvedValue({
      reference: "abc123ref",
      expiresAt: "2026-09-08T00:00:00Z",
    });

    const first = renderTiers();
    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: "Donate" }).getAttribute("href"),
      ).toContain("client_reference_id=abc123ref"),
    );
    expect(api.createDonationReference).toHaveBeenCalledTimes(1);

    first.unmount();
    renderTiers();
    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: "Donate" }).getAttribute("href"),
      ).toContain("client_reference_id=abc123ref"),
    );
    expect(api.createDonationReference).toHaveBeenCalledTimes(1);
  });
});

