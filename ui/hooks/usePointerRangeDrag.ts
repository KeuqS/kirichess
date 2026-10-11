"use client";

import { useCallback, useEffect, useRef, type PointerEvent } from "react";

interface UsePointerRangeDragOptions {
  min: number;
  max: number;
  step: number;
  onChange: (value: number) => void;
  disabled?: boolean;
}

/**
 * Belt-and-suspenders pointer handling for a native `<input type="range">`.
 *
 * The native element already jumps-to-click and drags past its own edges in every desktop
 * browser, and that native path is left fully in place: keyboard control, screen readers, and
 * the `accent-color`/pseudo-element styling all still come from the real control. This hook adds
 * an independent, pointer-position-driven value calculation on top of it, spread onto the input
 * as `{...usePointerRangeDrag(...)}`:
 *
 * - A click anywhere on the track jumps straight to that position - no need to land exactly on
 *   the (visually 18px, but CSS-height-8px-box) thumb.
 * - Dragging keeps tracking towards min/max even once the pointer strays outside the input's own
 *   thin box while the button stays held, because the calculation runs off `window`-level
 *   `pointermove`, not the input's own bounds.
 *
 * Both paths compute the same value from the same pointer position, so on a browser where the
 * native drag already behaves this is a harmless duplicate; on one where it doesn't (a stray
 * `pointercancel`, a touchpad driver that drops capture, an embedded webview), this is the path
 * that actually moves the thumb. See `AvatarCropper`'s crop-square pan for the same pattern.
 */
export function usePointerRangeDrag({
  min,
  max,
  step,
  onChange,
  disabled = false,
}: UsePointerRangeDragOptions) {
  const ref = useRef<HTMLInputElement>(null);
  const dragging = useRef(false);

  const valueFromClientX = useCallback(
    (clientX: number) => {
      const el = ref.current;
      if (!el) return null;
      const rect = el.getBoundingClientRect();
      if (rect.width <= 0) return null;
      const ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
      const raw = min + ratio * (max - min);
      const stepped = Math.round(raw / step) * step;
      return Math.min(max, Math.max(min, stepped));
    },
    [min, max, step],
  );

  // Refs so the window listener below can be registered once (not torn down and
  // re-added on every pixel of movement) while still calling the latest closures.
  const latestOnChange = useRef(onChange);
  latestOnChange.current = onChange;
  const latestValueFromClientX = useRef(valueFromClientX);
  latestValueFromClientX.current = valueFromClientX;

  useEffect(() => {
    function move(e: globalThis.PointerEvent) {
      if (!dragging.current) return;
      const next = latestValueFromClientX.current(e.clientX);
      if (next !== null) latestOnChange.current(next);
    }
    function stop() {
      dragging.current = false;
    }
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", stop);
    window.addEventListener("pointercancel", stop);
    return () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };
  }, []);

  const onPointerDown = useCallback(
    (e: PointerEvent<HTMLInputElement>) => {
      if (disabled) return;
      dragging.current = true;
      const next = valueFromClientX(e.clientX);
      if (next !== null) onChange(next);
    },
    [disabled, onChange, valueFromClientX],
  );

  return { ref, onPointerDown };
}
