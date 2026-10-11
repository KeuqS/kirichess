/**
 * Shared Tab-trap helper (P-5-2). Dialog needs this wrap
 * behaviour; duplicating the selector is how the two copies would drift.
 *
 * The heading / container that takes initial programmatic focus uses `tabIndex={-1}` and is
 * therefore excluded from the cycle - `:not([tabindex="-1"])` is load-bearing.
 */
export const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function focusableElements(container: HTMLElement): HTMLElement[] {
  return Array.from(
    container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR),
  );
}

/**
 * If Tab would leave `container`, move focus to the other end and preventDefault. Callers that
 * render a nested dialog must stopPropagation on Tab so an outer trap does not see the inner
 * dialog's controls as part of its cycle.
 */
export function trapTabKey(
  container: HTMLElement,
  event: {
    key: string;
    shiftKey: boolean;
    preventDefault: () => void;
  },
): void {
  if (event.key !== "Tab") return;
  const focusable = focusableElements(container);
  if (focusable.length === 0) return;
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = document.activeElement;
  if (event.shiftKey) {
    if (active === first || !container.contains(active)) {
      event.preventDefault();
      last.focus();
    }
  } else if (active === last || !container.contains(active)) {
    event.preventDefault();
    first.focus();
  }
}
