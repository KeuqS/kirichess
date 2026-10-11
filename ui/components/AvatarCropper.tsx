"use client";

import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type PointerEvent,
} from "react";
import { useTranslations } from "next-intl";
import { avatarCropDrawSize, clampCropOffset } from "@/lib/avatarImage";

/**
 * Halfway between the original 280px square and the full-dialog preview.
 * Narrow screens can still go smaller.
 */
const MAX_VIEWPORT = 420;

/** Used when the dialog has not been measured yet (jsdom, first frame). */
const FALLBACK_VIEWPORT = MAX_VIEWPORT;

/** Title, hint, zoom row, actions, and dialog padding, kept clear of the square. */
const CHROME_PX = 288;

interface AvatarCropperProps {
  image: HTMLImageElement;
  scale: number;
  offsetX: number;
  offsetY: number;
  onScaleChange: (scale: number) => void;
  onOffsetChange: (x: number, y: number) => void;
  /** Pixel size of the square the offsets are measured against. */
  onViewportSize: (size: number) => void;
}

export function AvatarCropper({
  image,
  scale,
  offsetX,
  offsetY,
  onScaleChange,
  onOffsetChange,
  onViewportSize,
}: AvatarCropperProps) {
  const t = useTranslations("AvatarPicker");
  const hostRef = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const last = useRef({ x: 0, y: 0 });
  const [viewport, setViewport] = useState(0);

  useLayoutEffect(() => {
    const el = hostRef.current;
    if (!el) return;

    const measure = () => {
      const width = Math.floor(el.clientWidth);
      if (width <= 0) {
        setViewport(FALLBACK_VIEWPORT);
        return;
      }
      const cap = Math.max(160, window.innerHeight - CHROME_PX);
      setViewport(Math.floor(Math.min(width, cap, MAX_VIEWPORT)));
    };

    measure();
    const observer =
      typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    observer?.observe(el);
    window.addEventListener("resize", measure);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, []);

  useEffect(() => {
    if (viewport > 0) onViewportSize(viewport);
  }, [viewport, onViewportSize]);

  const { drawW, drawH } =
    viewport > 0
      ? avatarCropDrawSize(
          image.naturalWidth,
          image.naturalHeight,
          viewport,
          scale,
        )
      : { drawW: 0, drawH: 0 };
  const shownX = viewport > 0 ? clampCropOffset(offsetX, drawW, viewport) : 0;
  const shownY = viewport > 0 ? clampCropOffset(offsetY, drawH, viewport) : 0;

  useEffect(() => {
    if (viewport <= 0) return;
    if (shownX !== offsetX || shownY !== offsetY) onOffsetChange(shownX, shownY);
  }, [viewport, shownX, shownY, offsetX, offsetY, onOffsetChange]);

  const left = viewport / 2 - drawW / 2 + shownX;
  const top = viewport / 2 - drawH / 2 + shownY;

  /*
   * Dragging is driven by `window`-level listeners, not the crop square's own
   * onPointerMove/onPointerUp. `setPointerCapture` below is the right tool for keeping the
   * pan going once the pointer leaves the (small, square) box, and real browsers honour it -
   * but it is one browser/OS combo away from silently not redirecting (older WebViews,
   * some Windows touchpad drivers producing a stray `pointercancel`), and when that happens
   * with only element-scoped handlers, panning stops dead the instant the cursor crosses the
   * edge, which reads exactly as "won't slide" or "stops when leaving the slider's box". A
   * window listener has no such edge: it fires wherever the pointer is, all the way to the
   * viewport boundary, independent of whether capture actually took.
   *
   * `dragMove` is a ref holding the latest closure so the window listener itself can be
   * registered once (below) instead of being torn down and re-added on every pixel of
   * movement, which is what a `useCallback([..., offsetX, offsetY, ...])` dependency list
   * would otherwise cause at `window` scope.
   */
  const dragMove = useRef<(e: globalThis.PointerEvent) => void>(() => {});
  dragMove.current = (e) => {
    if (!dragging.current || viewport <= 0) return;
    const dx = e.clientX - last.current.x;
    const dy = e.clientY - last.current.y;
    last.current = { x: e.clientX, y: e.clientY };
    onOffsetChange(
      clampCropOffset(offsetX + dx, drawW, viewport),
      clampCropOffset(offsetY + dy, drawH, viewport),
    );
  };

  const cropRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const move = (e: globalThis.PointerEvent) => dragMove.current(e);
    const stop = (e: globalThis.PointerEvent) => {
      dragging.current = false;
      const el = cropRef.current;
      if (el?.hasPointerCapture?.(e.pointerId)) el.releasePointerCapture(e.pointerId);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", stop);
    window.addEventListener("pointercancel", stop);
    return () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };
  }, []);

  const onPointerDown = useCallback((e: PointerEvent<HTMLDivElement>) => {
    dragging.current = true;
    last.current = { x: e.clientX, y: e.clientY };
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // Not supported everywhere (jsdom, some older browsers). The window listeners
      // above don't depend on it, so the pan still tracks past the box's edges.
    }
  }, []);

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-muted">{t("cropHint")}</p>
      <div ref={hostRef} className="w-full">
        <div
          ref={cropRef}
          className="relative mx-auto overflow-hidden border border-[var(--border)] bg-black/40 touch-none"
          style={
            viewport > 0
              ? { width: viewport, height: viewport }
              : { width: "100%", aspectRatio: "1" }
          }
          onPointerDown={onPointerDown}
        >
          {viewport > 0 ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={image.src}
              alt=""
              draggable={false}
              className="pointer-events-none absolute max-w-none select-none"
              style={{
                width: drawW,
                height: drawH,
                left,
                top,
              }}
            />
          ) : null}
          <div
            aria-hidden="true"
            className="pointer-events-none absolute inset-0 ring-1 ring-inset ring-white/20"
          />
        </div>
      </div>
      <label className="flex items-center gap-3 text-sm text-muted">
        <span className="shrink-0">{t("zoom")}</span>
        <input
          type="range"
          min={1}
          max={3}
          step={0.01}
          value={scale}
          onChange={(e) => onScaleChange(Number(e.target.value))}
          className="w-full accent-[var(--bounty)]"
        />
      </label>
    </div>
  );
}
