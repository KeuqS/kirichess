#!/usr/bin/env node
/**
 * AD.1: trim a Playwright raw webm to the clock-swing window and emit
 * postable MP4 + GIF. Requires ffmpeg on PATH and clip/queen-capture-raw.webm
 * (produced by e2e/press-kit/capture.spec.ts). GIF width is a named constant
 * sized for the desktop feed recapture, not the pulled 430px phone frame.
 *
 *   node convert.mjs
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const CLIP_DIR = path.join(__dirname, "clip");
const RAW = path.join(CLIP_DIR, "queen-capture-raw.webm");
const MP4 = path.join(CLIP_DIR, "queen-capture.mp4");
const GIF = path.join(CLIP_DIR, "queen-capture.gif");

/** Desktop feed GIF width (720-900 band). Used in both scale= filters. */
const GIF_WIDTH = 800;

/** SSEOF = "-12" is tuned for the opening-swing sequence in this spec
 *  (queue glow → four plies → Qxf6) and must be re-derived if the featured
 *  clip becomes the clutch-queen. */
const SSEOF = "-12";

function ffmpeg(args) {
  const result = spawnSync("ffmpeg", ["-hide_banner", "-y", ...args], {
    stdio: "inherit",
    shell: false,
  });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      console.error(
        "ffmpeg is not on PATH. Install it (winget install Gyan.FFmpeg) and retry.",
      );
      process.exit(1);
    }
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

ffmpeg([
  "-sseof",
  SSEOF,
  "-i",
  RAW,
  "-c:v",
  "libx264",
  "-pix_fmt",
  "yuv420p",
  "-crf",
  "20",
  "-movflags",
  "+faststart",
  MP4,
]);

const tmp = mkdtempSync(path.join(tmpdir(), "kirichess-presskit-"));
const palette = path.join(tmp, "palette.png");
try {
  ffmpeg([
    "-sseof",
    SSEOF,
    "-i",
    RAW,
    "-vf",
    `fps=12,scale=${GIF_WIDTH}:-1:flags=lanczos,palettegen=stats_mode=full`,
    "-update",
    "1",
    palette,
  ]);
  ffmpeg([
    "-sseof",
    SSEOF,
    "-i",
    RAW,
    "-i",
    palette,
    "-lavfi",
    `fps=12,scale=${GIF_WIDTH}:-1:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5`,
    GIF,
  ]);
} finally {
  rmSync(tmp, { recursive: true, force: true });
}

console.log(`Wrote ${MP4}`);
console.log(`Wrote ${GIF}`);
