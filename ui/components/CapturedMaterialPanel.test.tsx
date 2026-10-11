import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { CapturedMaterialPanel } from "./CapturedMaterialPanel";
import en from "../messages/en.json";

describe("CapturedMaterialPanel", () => {
  it("shows captured seconds in white, under a captured heading", () => {
    const { container } = render(
      <NextIntlClientProvider locale="en" messages={en}>
        <CapturedMaterialPanel
          opponentLabel="opponent"
          opponentPieces={["p"]}
          opponentSeconds={20}
          yourPieces={["n"]}
          yourSeconds={-5}
        />
      </NextIntlClientProvider>,
    );
    expect(screen.getByText("CAPTURED")).toBeInTheDocument();
    const opponent = screen.getByText("+20s");
    const you = screen.getByText("-5s");
    expect(opponent).toHaveClass("text-white");
    expect(you).toHaveClass("text-white");
    expect(container).toBeTruthy();
  });
});
