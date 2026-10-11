import { describe, expect, it } from "vitest";
import { appendReference } from "./donationLink";

describe("appendReference", () => {
  it("appends client_reference_id to a bare link", () => {
    expect(appendReference("https://buy.stripe.com/abc", "ref-1")).toBe(
      "https://buy.stripe.com/abc?client_reference_id=ref-1",
    );
  });

  it("returns the link unchanged when there is no reference", () => {
    expect(appendReference("https://buy.stripe.com/abc", null)).toBe(
      "https://buy.stripe.com/abc",
    );
  });

  it("merges into a link that already carries a query string", () => {
    expect(
      appendReference("https://buy.stripe.com/abc?locale=en", "ref-2"),
    ).toBe("https://buy.stripe.com/abc?locale=en&client_reference_id=ref-2");
  });

  it("returns the input unchanged rather than throwing on an unparseable link", () => {
    expect(appendReference("not-a-url", "ref-3")).toBe("not-a-url");
  });
});
