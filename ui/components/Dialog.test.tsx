import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Dialog } from "./Dialog";

describe("Dialog focus management (P-5-2)", () => {
  function renderDialog() {
    return render(
      <Dialog title="Confirm resign" onClose={vi.fn()}>
        <button type="button">Cancel</button>
        <button type="button">Resign</button>
      </Dialog>,
    );
  }

  it("wires role=dialog, aria-modal and aria-labelledby to the heading", () => {
    renderDialog();
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    const heading = screen.getByRole("heading", {
      level: 2,
      name: "Confirm resign",
    });
    expect(dialog.getAttribute("aria-labelledby")).toBe(heading.id);
  });

  it("moves focus to the first focusable control on mount", () => {
    renderDialog();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
  });

  it("restores focus to the element that was focused before the dialog mounted", () => {
    render(<button type="button">Outside control</button>);
    const outside = screen.getByRole("button", { name: "Outside control" });
    outside.focus();

    const { unmount } = renderDialog();
    expect(outside).not.toHaveFocus();

    unmount();
    expect(outside).toHaveFocus();
  });

  it("wraps Tab from the last focusable control back to the first", () => {
    renderDialog();
    const first = screen.getByRole("button", { name: "Cancel" });
    const last = screen.getByRole("button", { name: "Resign" });

    last.focus();
    fireEvent.keyDown(last, { key: "Tab" });
    expect(first).toHaveFocus();
  });

  it("wraps Shift+Tab from the first focusable control back to the last", () => {
    renderDialog();
    const first = screen.getByRole("button", { name: "Cancel" });
    const last = screen.getByRole("button", { name: "Resign" });

    first.focus();
    fireEvent.keyDown(first, { key: "Tab", shiftKey: true });
    expect(last).toHaveFocus();
  });

  it("calls onClose on Escape", () => {
    const onClose = vi.fn();
    render(
      <Dialog title="Confirm resign" onClose={onClose}>
        <button type="button">Cancel</button>
      </Dialog>,
    );
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("caps height to the dynamic viewport (P-6-4)", () => {
    const { container } = renderDialog();
    expect(container.querySelector(".dialog-viewport-max")).not.toBeNull();
  });
});
