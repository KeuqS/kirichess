import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { TwoFactorCard } from "./TwoFactorCard";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const api = vi.hoisted(() => ({
  getTwoFactorStatus: vi.fn(),
  beginTwoFactorEnrolment: vi.fn(),
  enableTwoFactor: vi.fn(),
  disableTwoFactor: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  googleSignInUrl: vi.fn(
    (path: string) => `https://api.example/google?returnPath=${path}`,
  ),
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
  beginTwoFactorEnrolment: api.beginTwoFactorEnrolment,
  enableTwoFactor: api.enableTwoFactor,
  disableTwoFactor: api.disableTwoFactor,
  regenerateRecoveryCodes: api.regenerateRecoveryCodes,
  googleSignInUrl: api.googleSignInUrl,
}));

function renderCard() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <TwoFactorCard />
    </NextIntlClientProvider>,
  );
}

describe("TwoFactorCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    sessionStorage.clear();
    useAuth.mockReturnValue({ token: "tok", adoptSession: vi.fn() });
    api.beginTwoFactorEnrolment.mockResolvedValue({
      sharedKey: "ABCD",
      otpauthUri: "otpauth://totp/KiriChess",
    });
  });

  it("shows a password field on enable when the account has a password", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: true,
    });
    renderCard();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Enable" }),
      ).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByRole("button", { name: "Enable" }));
    await waitFor(() =>
      expect(screen.getByLabelText("Password")).toBeInTheDocument(),
    );
  });

  it("shows Confirm with Google on enable when the account has no password", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: false,
    });
    renderCard();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Enable" }),
      ).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByRole("button", { name: "Enable" }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Confirm with Google" }),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("renders a QR code alongside the setup key during enrolment", async () => {
    api.getTwoFactorStatus.mockResolvedValue({
      enabled: false,
      recoveryCodesRemaining: 0,
      hasPassword: true,
    });
    renderCard();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Enable" }),
      ).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByRole("button", { name: "Enable" }));
    const qrCode = await screen.findByRole("img", {
      name: "QR code for two-factor authentication setup",
    });
    expect(qrCode.querySelector("svg")).toBeInTheDocument();
    // Same secret as the setup key text below it, not a second value.
    expect(screen.getByText("ABCD")).toBeInTheDocument();
  });

  it("does not fetch two-factor status when there is no token (C-10-10)", () => {
    useAuth.mockReturnValue({ token: null, adoptSession: vi.fn() });
    renderCard();
    expect(api.getTwoFactorStatus).not.toHaveBeenCalled();
  });

  it("drops a stale status response from a superseded call instead of overwriting the fresh one (C-10-10)", async () => {
    // First call (mount, tokenA) hangs; second call (after the token rotates while this card
    // stays mounted) resolves immediately with enabled:true. Without the requestId guard,
    // resolving the first call late with enabled:false would flip the card back to "off" after
    // it already rendered "on" - the same class of bug MatchHistoryCard.test.tsx pins for its
    // own on-mount fetch, here spanning loadStatus's two call sites (mount effect and manual
    // reload).
    let resolveStale!: (status: {
      enabled: boolean;
      recoveryCodesRemaining: number;
      hasPassword: boolean;
    }) => void;
    const stale = new Promise<{
      enabled: boolean;
      recoveryCodesRemaining: number;
      hasPassword: boolean;
    }>((resolve) => {
      resolveStale = resolve;
    });
    api.getTwoFactorStatus.mockReturnValueOnce(stale);
    api.getTwoFactorStatus.mockResolvedValueOnce({
      enabled: true,
      recoveryCodesRemaining: 9,
      hasPassword: true,
    });

    useAuth.mockReturnValue({ token: "tokenA", adoptSession: vi.fn() });
    const { rerender } = renderCard();

    await waitFor(() =>
      expect(api.getTwoFactorStatus).toHaveBeenCalledTimes(1),
    );

    useAuth.mockReturnValue({ token: "tokenB", adoptSession: vi.fn() });
    rerender(
      <NextIntlClientProvider locale="en" messages={en}>
        <TwoFactorCard />
      </NextIntlClientProvider>,
    );

    await waitFor(() =>
      expect(api.getTwoFactorStatus).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Regenerate codes" }),
      ).toBeInTheDocument(),
    );

    await act(async () => {
      resolveStale({
        enabled: false,
        recoveryCodesRemaining: 0,
        hasPassword: true,
      });
      await Promise.resolve();
    });

    // Still showing the "on" section (Regenerate/Disable), not knocked back to "off".
    expect(
      screen.getByRole("button", { name: "Regenerate codes" }),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Enable" })).toBeNull();
  });
});
