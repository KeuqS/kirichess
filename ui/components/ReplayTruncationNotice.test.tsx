import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import en from "../messages/en.json";
import { ReplayTruncationNotice } from "./ReplayTruncationNotice";

function renderNotice(truncatedAtPly: number | null | undefined) {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <ReplayTruncationNotice truncatedAtPly={truncatedAtPly} />
    </NextIntlClientProvider>,
  );
}

describe("ReplayTruncationNotice", () => {
  it("says nothing when every stored move replayed", () => {
    renderNotice(null);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("says nothing when the response omits the field", () => {
    renderNotice(undefined);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("names the ply playback stopped at", () => {
    renderNotice(4);
    expect(screen.getByRole("status")).toHaveTextContent(
      "Playback stops at ply 4",
    );
  });

  it("says the start could not be reconstructed when truncation is before any move", () => {
    renderNotice(0);
    expect(screen.getByRole("status")).toHaveTextContent(
      "starting position could not be reconstructed",
    );
  });
});
