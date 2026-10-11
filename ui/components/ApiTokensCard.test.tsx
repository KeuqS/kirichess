import { render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { ApiTokensCard } from "./ApiTokensCard";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const api = vi.hoisted(() => ({
  getApiTokens: vi.fn(),
  getTwoFactorStatus: vi.fn(),
  createApiToken: vi.fn(),
  revokeApiToken: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  },
  getApiTokens: api.getApiTokens,
  getTwoFactorStatus: api.getTwoFactorStatus,
  createApiToken: api.createApiToken,
  revokeApiToken: api.revokeApiToken,
}));

function renderCard() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <ApiTokensCard />
    </NextIntlClientProvider>,
  );
}

describe("ApiTokensCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuth.mockReturnValue({ token: "tok" });
    api.getApiTokens.mockResolvedValue([]);
  });

  it("asks for a password on create when the account has one", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: true,
    });
    renderCard();
    await waitFor(() =>
      expect(screen.getByLabelText("Password")).toBeInTheDocument(),
    );
  });

  it("does not ask for a password on create for a Google-only account", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: false,
    });
    renderCard();
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Create token" })).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("shows when a token expires", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: false,
    });
    api.getApiTokens.mockResolvedValue([
      {
        id: "1",
        name: "engine",
        tokenPrefix: "abcd1234",
        scopes: ["read"],
        createdAt: "2026-10-02T12:00:00Z",
        expiresAt: "2026-12-31T12:00:00Z",
        lastUsedAt: null,
      },
    ]);
    renderCard();
    await waitFor(() => expect(screen.getByText("engine")).toBeInTheDocument());
    expect(screen.getByText(/expires /)).toBeInTheDocument();
    expect(screen.getByText(/kirichess_pat_abcd1234/)).toBeInTheDocument();
  });
});
