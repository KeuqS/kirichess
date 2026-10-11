"use client";

import {
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type FormEvent,
} from "react";
import { useTranslations } from "next-intl";
import { IconPencil } from "@tabler/icons-react";
import { Avatar } from "@/components/Avatar";
import { Dialog } from "@/components/Dialog";
import { AvatarCropper } from "@/components/AvatarCropper";
import { useAuth } from "@/components/AuthProvider";
import { ApiError, clearAvatar, uploadAvatar } from "@/lib/api";
import { resolveAvatarUrl } from "@/lib/avatarUrl";
import {
  AVATAR_ACCEPT,
  AVATAR_MAX_BYTES,
  exportCroppedAvatar,
  loadImageFromFile,
  releaseLoadedImage,
} from "@/lib/avatarImage";

interface EditableAvatarProps {
  label: string;
  avatarUrl?: string | null;
  size?: "sm" | "lg";
  shape?: "circle" | "square";
}

type DialogMode = "menu" | "crop";

/**
 * Own-profile avatar with upload + square crop, or initials. Renders a plain `Avatar` when signed out.
 */
export function EditableAvatar({
  label,
  avatarUrl,
  size = "lg",
  shape = "square",
}: EditableAvatarProps) {
  const t = useTranslations("AvatarPicker");
  const { token, setAvatarUrl } = useAuth();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<DialogMode>("menu");
  const [cropImage, setCropImage] = useState<HTMLImageElement | null>(null);
  const [scale, setScale] = useState(1);
  const [offsetX, setOffsetX] = useState(0);
  const [offsetY, setOffsetY] = useState(0);
  const [cropViewport, setCropViewport] = useState(0);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const resolvedSrc = resolveAvatarUrl(avatarUrl);
  const hasPhoto = Boolean(avatarUrl);

  useEffect(() => {
    if (!open) return;
    setMode("menu");
    setCropImage(null);
    setScale(1);
    setOffsetX(0);
    setOffsetY(0);
    setCropViewport(0);
    setError(null);
  }, [open]);

  useEffect(() => {
    if (!cropImage) return;
    const image = cropImage;
    return () => releaseLoadedImage(image);
  }, [cropImage]);

  if (!token) {
    return <Avatar label={label} src={resolvedSrc} size={size} shape={shape} />;
  }

  async function handleFileChange(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;

    if (file.size > AVATAR_MAX_BYTES) {
      setError(t("fileTooLarge"));
      return;
    }

    try {
      const image = await loadImageFromFile(file);
      setCropImage(image);
      setScale(1);
      setOffsetX(0);
      setOffsetY(0);
      setMode("crop");
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("invalidFile"));
    }
  }

  async function handleUploadSave(e: FormEvent) {
    e.preventDefault();
    if (!cropImage || cropViewport <= 0) return;
    setSubmitting(true);
    setError(null);
    try {
      const blob = await exportCroppedAvatar(
        cropImage,
        scale,
        offsetX,
        offsetY,
        cropViewport,
      );
      const res = await uploadAvatar(blob);
      setAvatarUrl(res.avatarUrl);
      setOpen(false);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : err instanceof Error
            ? err.message
            : t("genericError"),
      );
    } finally {
      setSubmitting(false);
    }
  }

  async function handleUseInitials() {
    setSubmitting(true);
    setError(null);
    try {
      const res = await clearAvatar();
      setAvatarUrl(res.avatarUrl);
      setOpen(false);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : err instanceof Error
            ? err.message
            : t("genericError"),
      );
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={t("changeAvatar")}
        aria-haspopup="dialog"
        aria-expanded={open}
        className="group relative shrink-0 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--bounty)]"
      >
        <Avatar label={label} src={resolvedSrc} size={size} shape={shape} />
        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 bg-black/0 transition-colors group-hover:bg-black/45 group-focus-visible:bg-black/45"
        />
        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 flex items-center justify-center text-white opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100"
        >
          <IconPencil size={size === "lg" ? 28 : 16} stroke={2} />
        </span>
      </button>

      <input
        ref={fileInputRef}
        type="file"
        accept={AVATAR_ACCEPT}
        className="sr-only"
        onChange={handleFileChange}
      />

      {open ? (
        mode === "menu" ? (
          <Dialog title={t("dialogTitle")} onClose={() => setOpen(false)}>
            <p className="mt-1 text-sm text-muted">{t("dialogDescription")}</p>

            <div className="mt-6 flex flex-col gap-3">
              <button
                type="button"
                disabled={submitting}
                onClick={() => fileInputRef.current?.click()}
                className="btn-primary w-full py-2.5 text-sm"
              >
                {t("uploadPhoto")}
              </button>
              {hasPhoto ? (
                <button
                  type="button"
                  disabled={submitting}
                  onClick={() => void handleUseInitials()}
                  className="btn-secondary w-full py-2.5 text-sm"
                >
                  {t("useInitials")}
                </button>
              ) : null}
            </div>

            {error ? <div className="error-banner mt-4">{error}</div> : null}

            <div className="mt-6 flex justify-end">
              <button
                type="button"
                onClick={() => setOpen(false)}
                className="btn-secondary px-5 py-2 text-sm"
              >
                {t("cancel")}
              </button>
            </div>
          </Dialog>
        ) : (
          <Dialog title={t("cropTitle")} onClose={() => setOpen(false)} size="lg">
            <form onSubmit={handleUploadSave}>
              {cropImage ? (
                <div className="mt-4">
                  <AvatarCropper
                    image={cropImage}
                    scale={scale}
                    offsetX={offsetX}
                    offsetY={offsetY}
                    onScaleChange={setScale}
                    onOffsetChange={(x, y) => {
                      setOffsetX(x);
                      setOffsetY(y);
                    }}
                    onViewportSize={setCropViewport}
                  />
                </div>
              ) : null}

              {error ? <div className="error-banner mt-4">{error}</div> : null}

              <div className="mt-6 flex justify-end gap-2">
                <button
                  type="button"
                  disabled={submitting}
                  onClick={() => {
                    setMode("menu");
                    setCropImage(null);
                    setError(null);
                  }}
                  className="btn-secondary px-3 py-1.5 text-sm"
                >
                  {t("back")}
                </button>
                <button
                  type="submit"
                  disabled={submitting || !cropImage || cropViewport <= 0}
                  className="btn-primary px-3 py-1.5 text-sm"
                >
                  {t("save")}
                </button>
              </div>
            </form>
          </Dialog>
        )
      ) : null}
    </>
  );
}
