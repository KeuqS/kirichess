"use client";

import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from "react";
import { Card } from "@/components/Card";
import { focusableElements, trapTabKey } from "@/lib/focusTrap";

const SIZE_CLASS = {
  md: "max-w-md",
  lg: "max-w-lg",
  xl: "max-w-2xl",
} as const;

interface DialogProps {
  /** Heading shown at the top of the card and wired to `aria-labelledby`. */
  title: ReactNode;
  /** Escape and a backdrop click both call this. */
  onClose: () => void;
  children: ReactNode;
  size?: keyof typeof SIZE_CLASS;
  /** Extra classes for the card itself, not the backdrop. */
  className?: string;
}

/**
 * The one modal shell. Before this existed the same backdrop + card markup was copy-pasted five
 * times across the moderation pages and the standalone dialogs, and the copies had drifted: three
 * used `z-[60] bg-black/70 px-4`, two used `z-50 bg-black/60 px-6`, and the moderation ones carried
 * no `role="dialog"` or `aria-modal` at all.
 *
 * Escape-to-close and backdrop-click-to-close live here so no caller has to remember them.
 * Elevation is `shadow-2xl`, the overlay tier in `.claude/rules/styling.md`.
 *
 * P-5-2: initial focus, a Tab trap, and focus restore on unmount. Tab is stopped from bubbling
 * so a nested dialog is not swept into an outer trap.
 */
export function Dialog({
  title,
  onClose,
  children,
  size = "md",
  className = "",
}: DialogProps) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const previouslyFocused =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;

    const container = dialogRef.current;
    const first = container ? focusableElements(container)[0] : undefined;
    (first ?? container)?.focus();

    return () => {
      if (previouslyFocused && document.body.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    };
    // Mount/unmount only. Callers pass an inline onClose (GameControls, ReportDialog), and
    // putting it in this effect's deps would steal focus back to the first control on every
    // parent render.
  }, []);

  useEffect(() => {
    function onKeyDown(e: globalThis.KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  function handleKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key !== "Tab") return;
    e.stopPropagation();
    if (dialogRef.current) trapTabKey(dialogRef.current, e);
  }

  return (
    <div
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/70 px-4"
      // A click that starts inside the card and ends on the backdrop (drag-selecting text) must not
      // close the dialog, so this fires only when the backdrop is both target and currentTarget.
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <Card
        padding="p-6"
        className={`dialog-viewport-max w-full ${SIZE_CLASS[size]} text-left shadow-2xl ${className}`}
      >
        <div
          ref={dialogRef}
          role="dialog"
          aria-modal="true"
          aria-labelledby={titleId}
          tabIndex={-1}
          onKeyDown={handleKeyDown}
        >
          <h2 id={titleId} className="text-lg font-bold text-white">
            {title}
          </h2>
          {children}
        </div>
      </Card>
    </div>
  );
}
