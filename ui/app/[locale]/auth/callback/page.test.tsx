import { render, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "@/messages/en.json";
import { COUNTER_KEYS } from "@/lib/analytics";
import AuthCallbackPage from "./page";

const replace = vi.hoisted(() => vi.fn());
const completeExternalLogin = vi.hoisted(() => vi.fn());
const params = vi.hoisted(() => ({
  current: new URLSearchParams("code=one-time"),
}));
const api = vi.hoisted(() => ({
  exchangeExternalCode: vi.fn(),
  recordCounter: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useSearchParams: () => params.current,
}));

vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ replace }),
  Link: ({
    children,
    href,
  }: {
    children: React.ReactNode;
    href: string;
  }) => <a href={href}>{children}</a>,
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({
    completeExternalLogin,
    token: null,
    hydrated: true,
  }),
}));

vi.mock("@/lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api")>();
  return {
    ...actual,
    exchangeExternalCode: api.exchangeExternalCode,
    recordCounter: api.recordCounter,
  };
});

const SESSION = {
  userId: "user-1",
  email: "ada@example.com",
  displayName: "Ada",
  token: "access",
  refreshToken: "refresh",
  expiresAt: "2026-10-04T00:00:00Z",
  roles: [] as string[],
};

function renderCallback() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <AuthCallbackPage />
    </NextIntlClientProvider>,
  );
}

describe("OAuth callback signup counter (M-4-1)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    params.current = new URLSearchParams("code=one-time");
  });

  it("fires the signup counter when the exchange says the account was just created", async () => {
    api.exchangeExternalCode.mockResolvedValue({ ...SESSION, created: true });

    renderCallback();

    await waitFor(() => expect(completeExternalLogin).toHaveBeenCalledTimes(1));
    expect(api.recordCounter).toHaveBeenCalledTimes(1);
    expect(api.recordCounter).toHaveBeenCalledWith(COUNTER_KEYS.signup);
    expect(replace).toHaveBeenCalledWith("/");
  });

  it("does not fire the signup counter for a returning Google sign-in", async () => {
    api.exchangeExternalCode.mockResolvedValue({ ...SESSION, created: false });

    renderCallback();

    await waitFor(() => expect(completeExternalLogin).toHaveBeenCalledTimes(1));
    expect(api.recordCounter).not.toHaveBeenCalled();
    expect(replace).toHaveBeenCalledWith("/");
  });

  it("does not fire the signup counter when the exchange is a second-factor challenge", async () => {
    api.exchangeExternalCode.mockResolvedValue({
      twoFactorRequired: true,
      twoFactorTicket: "ticket",
      expiresInSeconds: 300,
    });

    renderCallback();

    await waitFor(() =>
      expect(api.exchangeExternalCode).toHaveBeenCalledWith("one-time"),
    );
    expect(api.recordCounter).not.toHaveBeenCalled();
    expect(completeExternalLogin).not.toHaveBeenCalled();
  });
});
