import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it, vi } from "vitest";
import { CustomGameCta } from "./CustomGameCta";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const labels = {
  buildLabel: "Build a game",
  signInLabel: "Sign in to build a game",
};

// 9f.4. `Link` comes from `@/i18n/navigation` now, and it reads the active locale from context to
// decide whether to prefix the href. No messages are needed (this component takes its labels as
// props), but the provider is: without one, next-intl throws before any assertion runs.
function renderCta() {
  return render(
    <NextIntlClientProvider locale="en" messages={{}}>
      <CustomGameCta {...labels} />
    </NextIntlClientProvider>,
  );
}

describe("CustomGameCta", () => {
  it("sends guests to sign in with a primary CTA", () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    renderCta();
    const link = screen.getByRole("link", { name: "Sign in to build a game" });
    expect(link).toHaveAttribute("href", "/login?next=%2Fcustom");
    expect(link).toHaveClass("btn-primary");
  });

  it("sends signed-in players to the builder with a primary CTA", () => {
    useAuth.mockReturnValue({ token: "session", hydrated: true });
    renderCta();
    const link = screen.getByRole("link", { name: "Build a game" });
    expect(link).toHaveAttribute("href", "/custom");
    expect(link).toHaveClass("btn-primary");
  });
});
