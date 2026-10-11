import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { EmailConfirmationBanner } from "./EmailConfirmationBanner";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const api = vi.hoisted(() => ({
  emailStatus: vi.fn(),
  resendConfirmation: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  emailStatus: api.emailStatus,
  resendConfirmation: api.resendConfirmation,
}));

function renderBanner() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <EmailConfirmationBanner />
    </NextIntlClientProvider>,
  );
}

describe("EmailConfirmationBanner", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuth.mockReturnValue({ token: "t", hydrated: true });
    api.resendConfirmation.mockResolvedValue({ sent: true });
  });

  it("renders nothing for a signed-out visitor, and does not call the API", () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    const { container } = renderBanner();

    // The lobby is public: a 401 for every anonymous visitor is the thing being avoided here.
    expect(api.emailStatus).not.toHaveBeenCalled();
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing for an unconfirmed account that is not blocked", async () => {
    // The grandfathered case. Nagging a player about a link they never needed is the bug.
    api.emailStatus.mockResolvedValue({
      email: "a@example.com",
      confirmed: false,
      playBlocked: false,
    });

    const { container } = renderBanner();
    await waitFor(() => expect(api.emailStatus).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("shows the address and resends on demand when blocked", async () => {
    api.emailStatus.mockResolvedValue({
      email: "blocked@example.com",
      confirmed: false,
      playBlocked: true,
    });

    renderBanner();

    expect(
      await screen.findByText(/blocked@example\.com/),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: en.EmailConfirmationBanner.resend }));

    await waitFor(() =>
      expect(api.resendConfirmation).toHaveBeenCalledWith("blocked@example.com"),
    );
    expect(
      await screen.findByText(en.EmailConfirmationBanner.sent),
    ).toBeInTheDocument();
  });

  it("reports a failed resend without losing the banner", async () => {
    api.emailStatus.mockResolvedValue({
      email: "blocked@example.com",
      confirmed: false,
      playBlocked: true,
    });
    api.resendConfirmation.mockRejectedValue(new Error("network"));

    renderBanner();
    fireEvent.click(
      await screen.findByRole("button", { name: en.EmailConfirmationBanner.resend }),
    );

    expect(
      await screen.findByText(en.EmailConfirmationBanner.failed),
    ).toBeInTheDocument();
    // Still offering the retry: a failed send must not strand the one way out of the gate.
    expect(
      screen.getByRole("button", { name: en.EmailConfirmationBanner.resend }),
    ).toBeInTheDocument();
  });

  it("stays silent when the status call fails", async () => {
    // Neither the lobby nor the profile may break because this call failed.
    api.emailStatus.mockRejectedValue(new Error("boom"));

    const { container } = renderBanner();
    await waitFor(() => expect(api.emailStatus).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });
});
