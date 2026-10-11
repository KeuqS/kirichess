import { afterEach, describe, expect, it, vi } from "vitest";
import {
  avatarCropDrawSize,
  clampCropOffset,
  loadImageFromFile,
  releaseLoadedImage,
} from "./avatarImage";

describe("clampCropOffset", () => {
  it("stops the pan where the photo still covers the square", () => {
    const viewport = 400;
    const { drawW, drawH } = avatarCropDrawSize(800, 400, viewport, 1);
    expect(drawH).toBe(400);
    expect(drawW).toBe(800);
    expect(clampCropOffset(0, drawH, viewport)).toBe(0);
    expect(clampCropOffset(999, drawW, viewport)).toBe(200);
    expect(clampCropOffset(-999, drawW, viewport)).toBe(-200);

    const edge = clampCropOffset(999, drawW, viewport);
    const left = viewport / 2 - drawW / 2 + edge;
    expect(left).toBe(0);
    expect(left + drawW).toBeGreaterThanOrEqual(viewport);
  });

  it("allows more pan once the photo is zoomed in", () => {
    const viewport = 400;
    const fitted = avatarCropDrawSize(400, 400, viewport, 1);
    const zoomed = avatarCropDrawSize(400, 400, viewport, 2);
    expect(clampCropOffset(50, fitted.drawW, viewport)).toBe(0);
    expect(clampCropOffset(50, zoomed.drawW, viewport)).toBe(50);
    expect(clampCropOffset(500, zoomed.drawW, viewport)).toBe(200);
  });
});

describe("loadImageFromFile", () => {
  const urlApi = URL as unknown as {
    createObjectURL?: (obj: Blob) => string;
    revokeObjectURL?: (url: string) => void;
  };
  const previousCreate = urlApi.createObjectURL;
  const previousRevoke = urlApi.revokeObjectURL;

  afterEach(() => {
    if (previousCreate) urlApi.createObjectURL = previousCreate;
    else delete urlApi.createObjectURL;
    if (previousRevoke) urlApi.revokeObjectURL = previousRevoke;
    else delete urlApi.revokeObjectURL;
    vi.unstubAllGlobals();
  });

  it("keeps the blob URL alive until the preview releases it", async () => {
    const revoked: string[] = [];
    urlApi.createObjectURL = () => "blob:avatar-preview";
    urlApi.revokeObjectURL = (url: string) => {
      revoked.push(url);
    };

    class FakeImage {
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      naturalWidth = 640;
      naturalHeight = 480;
      #src = "";

      set src(value: string) {
        this.#src = value;
        queueMicrotask(() => this.onload?.());
      }

      get src() {
        return this.#src;
      }
    }
    vi.stubGlobal("Image", FakeImage);

    const image = await loadImageFromFile(
      new File([new Uint8Array([1, 2, 3])], "photo.png", { type: "image/png" }),
    );

    expect(image.src).toBe("blob:avatar-preview");
    expect(revoked).toEqual([]);

    releaseLoadedImage(image);
    expect(revoked).toEqual(["blob:avatar-preview"]);
  });
});
