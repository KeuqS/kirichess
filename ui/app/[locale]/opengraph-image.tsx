import { ImageResponse } from "next/og";
import { SITE_NAME, SITE_DESCRIPTION } from "@/lib/site";

export const alt = SITE_NAME;
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

// File-convention default OG/Twitter card for every route that doesn't define its own (e.g.
// replay/[id], which overrides this with the specific game's result). Kept to a single default
// export per Next's metadata-route rules, and to plain flexbox/text: satori (which renders these)
// only supports a CSS subset, and this app has no logo asset to embed (see phase9d6 plan doc).
export default async function Image() {
  return new ImageResponse(
    <div
      style={{
        width: "100%",
        height: "100%",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        backgroundColor: "#282c34",
        color: "#abb2bf",
        fontFamily: "sans-serif",
      }}
    >
      <div
        style={{
          display: "flex",
          fontSize: 96,
          fontWeight: 700,
          color: "#e5c07b",
        }}
      >
        {SITE_NAME}
      </div>
      <div style={{ display: "flex", marginTop: 24, fontSize: 36 }}>
        {SITE_DESCRIPTION}
      </div>
    </div>,
    { ...size },
  );
}
