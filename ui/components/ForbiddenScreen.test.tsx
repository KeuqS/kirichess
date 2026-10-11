import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { ForbiddenScreen, StaffOnly } from "./ForbiddenScreen";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    className,
  }: {
    href: string;
    children: ReactNode;
    className?: string;
  }) => (
    <a href={href} className={className}>
      {children}
    </a>
  ),
}));

vi.mock("./ForbiddenScene", () => ({
  ForbiddenScene: () => <div data-testid="forbidden-scene" />,
}));

const messages = {
  forbidden: {
    title: "Moderators only",
    code: "403",
    heading: "You don't capture the king",
    body: "This page is only for moderators.",
    backHome: "Back to the lobby",
    learnCta: "Learn the rules",
  },
  RequireAuth: {
    loading: "Loading…",
  },
};

function renderScreen() {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <ForbiddenScreen />
    </NextIntlClientProvider>,
  );
}

describe("ForbiddenScreen", () => {
  it("tells the player this surface is for moderators, with the 404's two ways out", () => {
    renderScreen();
    expect(screen.getByTestId("forbidden-scene")).toBeTruthy();
    expect(screen.getByText("403")).toBeTruthy();
    expect(
      screen.getByRole("heading", { name: "You don't capture the king" }),
    ).toBeTruthy();
    expect(screen.getByText("This page is only for moderators.")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Back to the lobby" })).toHaveAttribute(
      "href",
      "/",
    );
    expect(screen.getByRole("link", { name: "Learn the rules" })).toHaveAttribute(
      "href",
      "/learn",
    );
  });
});

describe("StaffOnly", () => {
  it("waits for auth to hydrate before deciding", () => {
    useAuth.mockReturnValue({ isAdmin: false, hydrated: false });
    render(
      <NextIntlClientProvider locale="en" messages={messages}>
        <StaffOnly>
          <p>secret dashboard</p>
        </StaffOnly>
      </NextIntlClientProvider>,
    );
    expect(screen.getByText("Loading…")).toBeTruthy();
    expect(screen.queryByText("403")).toBeNull();
    expect(screen.queryByText("secret dashboard")).toBeNull();
  });

  it("renders the 403 screen when the viewer is not an admin", () => {
    useAuth.mockReturnValue({ isAdmin: false, hydrated: true });
    render(
      <NextIntlClientProvider locale="en" messages={messages}>
        <StaffOnly>
          <p>secret dashboard</p>
        </StaffOnly>
      </NextIntlClientProvider>,
    );
    expect(
      screen.getByRole("heading", { name: "You don't capture the king" }),
    ).toBeTruthy();
    expect(screen.queryByText("secret dashboard")).toBeNull();
  });

  it("renders children for an admin", () => {
    useAuth.mockReturnValue({ isAdmin: true, hydrated: true });
    render(
      <NextIntlClientProvider locale="en" messages={messages}>
        <StaffOnly>
          <p>secret dashboard</p>
        </StaffOnly>
      </NextIntlClientProvider>,
    );
    expect(screen.getByText("secret dashboard")).toBeTruthy();
    expect(screen.queryByText("403")).toBeNull();
  });
});
