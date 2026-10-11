import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { PatronIcon, PATRON_TIERS } from "./PatronIcon";

const messages = {
  DonationTiers: {
    patronBadgeLabel: "Patron",
  },
};

function renderIcon(
  props: Partial<React.ComponentProps<typeof PatronIcon>> = {},
) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <PatronIcon tier="None" {...props} />
    </NextIntlClientProvider>,
  );
}

describe("PatronIcon", () => {
  it("renders nothing for None", () => {
    const { container } = renderIcon({ tier: "None" });
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing for an unrecognised tier", () => {
    const { container } = renderIcon({ tier: "Emperor" });
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing for undefined", () => {
    const { container } = renderIcon({ tier: undefined });
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing for null", () => {
    const { container } = renderIcon({ tier: null });
    expect(container).toBeEmptyDOMElement();
  });

  it.each(PATRON_TIERS)(
    "renders the same bounty heart for the %s tier",
    (tier) => {
      const { container } = renderIcon({ tier });
      const svg = container.querySelector("svg");
      expect(svg).not.toBeNull();
      expect(svg?.getAttribute("class")).toContain("tabler-icon-heart");
      expect(svg?.getAttribute("class")).toContain("text-[var(--bounty)]");
    },
  );

  it("labels the badge as Patron, not a tier name", () => {
    const { container } = renderIcon({ tier: "Queen" });
    const labelled = container.querySelector('[role="img"]');
    expect(labelled).toHaveAttribute("aria-label", "Patron");
    expect(container.querySelector("svg")).toHaveAttribute(
      "aria-hidden",
      "true",
    );
  });

  it("defaults to the outline heart when kind is absent", () => {
    const { container } = renderIcon({ tier: "Pawn" });
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("class")).toContain("tabler-icon-heart ");
  });

  it("renders the outline heart for a one-time patron", () => {
    const { container } = renderIcon({ tier: "Pawn", kind: "OneTime" });
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("class")).toContain("tabler-icon-heart ");
  });

  it("renders the filled heart for a monthly patron", () => {
    const { container } = renderIcon({ tier: "Pawn", kind: "Monthly" });
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("class")).toContain("tabler-icon-heart-filled");
  });
});
