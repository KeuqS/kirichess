import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConnectionStatus, type ConnectionState } from "./ConnectionStatus";
import en from "../messages/en.json";

function renderState(
  state: ConnectionState,
  forfeitWarning?: boolean,
) {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <ConnectionStatus state={state} forfeitWarning={forfeitWarning} />
    </NextIntlClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ConnectionStatus", () => {
  it("renders nothing while the connection is up", () => {
    const { container } = renderState("online");
    expect(container).toBeEmptyDOMElement();
  });

  it("shows a notice banner, not a dialog, while reconnecting", () => {
    renderState("reconnecting");
    const banner = screen.getByText(en.ConnectionStatus.reconnecting);
    // styling.md: `notice-banner` is the shared recipe, `error-banner` means something else and a
    // reconnect that usually succeeds is not an error.
    expect(banner).toHaveClass("notice-banner");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("names the 30s forfeit timer on the player banner (P-4-4)", () => {
    renderState("reconnecting", true);
    expect(
      screen.getByText(en.ConnectionStatus.reconnectingWithForfeit),
    ).toHaveClass("notice-banner");
    expect(
      screen.queryByText(en.ConnectionStatus.reconnecting),
    ).not.toBeInTheDocument();
  });

  it("blocks with a dialog naming the state once the connection is closed for good", () => {
    renderState("closed");
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(dialog).toHaveTextContent(en.ConnectionStatus.closedTitle);
    expect(dialog).toHaveTextContent(en.ConnectionStatus.closedBody);
    // The banner belongs to the other state: a closed connection must not read as one still trying.
    expect(
      screen.queryByText(en.ConnectionStatus.reconnecting),
    ).not.toBeInTheDocument();
  });

  it("reloads the page from the dialog's only action", () => {
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    renderState("closed");

    fireEvent.click(
      screen.getByRole("button", { name: en.ConnectionStatus.reload }),
    );
    expect(reload).toHaveBeenCalledOnce();
  });

  it("offers Reload on a failed resync rather than a bare sentence (P-4-7)", () => {
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    renderState("resyncFailed");

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByText(en.ConnectionStatus.resyncFailed)).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole("button", { name: en.ConnectionStatus.reload }),
    );
    expect(reload).toHaveBeenCalledOnce();
  });
});
