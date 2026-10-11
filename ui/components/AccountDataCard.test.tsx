import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { AccountDataCard } from "./AccountDataCard";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  useRouter: () => ({ push: vi.fn() }),
}));

const api = vi.hoisted(() => ({
  getTwoFactorStatus: vi.fn(),
  deleteAccount: vi.fn(),
  exportAccountData: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  },
  getTwoFactorStatus: api.getTwoFactorStatus,
  deleteAccount: api.deleteAccount,
  exportAccountData: api.exportAccountData,
}));

function renderCard() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <AccountDataCard />
    </NextIntlClientProvider>,
  );
}

describe("AccountDataCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuth.mockReturnValue({ token: "tok", logout: vi.fn() });
  });

  it("asks for a password on delete when the account has one", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: true,
    });
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: /Delete account/i }));
    await waitFor(() =>
      expect(screen.getByLabelText("Password")).toBeInTheDocument(),
    );
  });

  it("does not ask for a password on delete for a Google-only account", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: false,
    });
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: /Delete account/i }));
    await waitFor(() =>
      expect(screen.getByText("This will erase your account.")).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText("Password")).toBeNull();
  });
});
