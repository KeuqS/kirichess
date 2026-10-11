import sharp from "sharp";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..", "public");

async function main() {
  const logo = path.join(root, "logo.png");
  const transparent = path.join(root, "logo-transparent.png");

  await sharp(logo)
    .resize(192, 192)
    .png()
    .toFile(path.join(root, "icons", "icon-192.png"));

  // Maskable: full-bleed brand background, no pre-rounded corners, glyph
  // within the OS safe zone (logo-transparent.png's glyph already sits at
  // the same ~62.5% scale/centering baked into logo.svg's icon layer).
  await sharp({
    create: {
      width: 512,
      height: 512,
      channels: 4,
      background: "#282c34",
    },
  })
    .composite([{ input: transparent }])
    .png()
    .toFile(path.join(root, "icons", "icon-maskable-512.png"));

  await sharp(logo)
    .resize(180, 180)
    .png()
    .toFile(path.join(root, "..", "app", "apple-icon.png"));

  console.log("done");
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
