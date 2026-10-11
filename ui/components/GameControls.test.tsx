import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import { GameControls } from "./GameControls";
import en from "../messages/en.json";

function renderControls(
  overrides: Partial<ComponentProps<typeof GameControls>> = {},
) {
  const props: ComponentProps<typeof GameControls> = {
    onOfferDraw: vi.fn(),
    onResign: vi.fn(),
    disabled: false,
    showDraw: true,
    ...overrides,
  };
  return {
    ...render(
      <NextIntlClientProvider locale="en" messages={en}>
        <GameControls {...props} />
      </NextIntlClientProvider>,
    ),
    props,
  };
}

describe("GameControls", () => {
  it("hides Offer draw when showDraw is false", () => {
    renderControls({ showDraw: false });
    expect(screen.queryByRole("button", { name: "Offer draw" })).toBeNull();
    expect(screen.getByRole("button", { name: "Resign" })).toBeInTheDocument();
  });

  it("does not resign until the dialog is confirmed", () => {
    const { props } = renderControls();
    fireEvent.click(screen.getByRole("button", { name: "Resign" }));
    expect(screen.getByRole("dialog", { name: "Resign?" })).toBeInTheDocument();
    expect(props.onResign).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(props.onResign).not.toHaveBeenCalled();
  });

  it("confirms resign from the dialog", () => {
    const { props } = renderControls();
    fireEvent.click(screen.getByRole("button", { name: "Resign" }));
    fireEvent.click(screen.getAllByRole("button", { name: "Resign" })[1]);
    expect(props.onResign).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("uses btn-danger on the idle Resign control", () => {
    renderControls();
    expect(screen.getByRole("button", { name: "Resign" })).toHaveClass(
      "btn-danger",
    );
  });

  it("disables Offer draw and Resign when disabled (P-4-3)", () => {
    const { props } = renderControls({ disabled: true });
    expect(screen.getByRole("button", { name: "Offer draw" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Resign" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Resign" }));
    expect(props.onResign).not.toHaveBeenCalled();
  });

  it("uses a 44px minimum height on in-game controls (P-6-7)", () => {
    renderControls();
    expect(screen.getByRole("button", { name: "Offer draw" })).toHaveClass(
      "min-h-11",
    );
    expect(screen.getByRole("button", { name: "Resign" })).toHaveClass(
      "min-h-11",
    );
  });
});
