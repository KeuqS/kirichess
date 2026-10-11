/** Matches `JpegAvatar.OutputEdge` on the server. */
export const AVATAR_OUTPUT_SIZE = 256;

/** Matches `JpegAvatar.MaxBytes` on the server. */
export const AVATAR_MAX_BYTES = 256 * 1024;

export const AVATAR_ACCEPT = "image/jpeg,image/png,image/webp,image/gif";

export function loadImageFromFile(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error("Could not read that image."));
    };
    image.src = url;
  });
}

/**
 * Drops the blob URL created by `loadImageFromFile`. Call this only after the
 * preview `<img src={image.src}>` has unmounted - revoking earlier leaves the
 * crop overlay with a broken image. The decoded bitmap stays usable for
 * `drawImage` either way; the preview does not.
 */
export function releaseLoadedImage(image: HTMLImageElement): void {
  const src = image.src;
  if (src.startsWith("blob:")) URL.revokeObjectURL(src);
}

/** Drawn size of the source in viewport pixels. `scale` is 1 at cover-fit. */
export function avatarCropDrawSize(
  naturalWidth: number,
  naturalHeight: number,
  viewportSize: number,
  scale: number,
): { drawW: number; drawH: number } {
  const cover =
    Math.max(viewportSize / naturalWidth, viewportSize / naturalHeight) * scale;
  return {
    drawW: naturalWidth * cover,
    drawH: naturalHeight * cover,
  };
}

/**
 * Pan limit so the photo always covers the crop square. Offset is a pixel shift
 * from center; past this, an edge of the photo would slide inside the square.
 */
export function clampCropOffset(
  offset: number,
  drawSize: number,
  viewportSize: number,
): number {
  const limit = Math.max(0, (drawSize - viewportSize) / 2);
  return Math.min(limit, Math.max(-limit, offset));
}

/**
 * Renders the visible square viewport (panned/zoomed) into a square JPEG blob for upload.
 * `offsetX` / `offsetY` are pixel shifts in viewport space; `scale` is 1 at "cover" fit.
 * Offsets are clamped the same way as the crop preview, so a pan past the edge
 * cannot bake empty borders into the file.
 */
export function exportCroppedAvatar(
  image: HTMLImageElement,
  scale: number,
  offsetX: number,
  offsetY: number,
  viewportSize: number,
): Promise<Blob> {
  const canvas = document.createElement("canvas");
  canvas.width = AVATAR_OUTPUT_SIZE;
  canvas.height = AVATAR_OUTPUT_SIZE;
  const ctx = canvas.getContext("2d");
  if (!ctx) return Promise.reject(new Error("Canvas is not available."));

  const { drawW, drawH } = avatarCropDrawSize(
    image.naturalWidth,
    image.naturalHeight,
    viewportSize,
    scale,
  );
  const panX = clampCropOffset(offsetX, drawW, viewportSize);
  const panY = clampCropOffset(offsetY, drawH, viewportSize);
  const ratio = AVATAR_OUTPUT_SIZE / viewportSize;
  const cx = AVATAR_OUTPUT_SIZE / 2 + panX * ratio;
  const cy = AVATAR_OUTPUT_SIZE / 2 + panY * ratio;

  ctx.drawImage(image, cx - drawW * ratio * 0.5, cy - drawH * ratio * 0.5, drawW * ratio, drawH * ratio);

  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (!blob) {
          reject(new Error("Could not encode the image."));
          return;
        }
        if (blob.size > AVATAR_MAX_BYTES) {
          reject(new Error("Profile picture is too large after cropping."));
          return;
        }
        resolve(blob);
      },
      "image/jpeg",
      0.9,
    );
  });
}
