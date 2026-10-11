import { ImageResponse } from "next/og";
import { getReplay, ApiError } from "@/lib/api";
import { replayCardText, replayCardSwing } from "@/lib/replayCard";
import { SITE_NAME } from "@/lib/site";

export const alt = "Replay result";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export default async function Image({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;

  let title = "Replay";
  let description = "Watch the replay on KiriChess.";
  let swing: ReturnType<typeof replayCardSwing> = null;
  try {
    // Server-only route: see the API_BASE_INTERNAL comment in replay/[id]/layout.tsx.
    const replay = await getReplay(id, process.env.API_BASE_INTERNAL);
    ({ title, description } = replayCardText(replay));
    swing = replayCardSwing(replay);
  } catch (err) {
    if (!(err instanceof ApiError && err.status === 404)) {
      // Rethrow anything that isn't "no such replay": a transient API outage should fail the
      // image request (Next serves the site default opengraph-image instead) rather than render a
      // misleadingly generic card.
      throw err;
    }
  }

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
        padding: "0 80px",
      }}
    >
      <div
        style={{
          display: "flex",
          fontSize: 28,
          fontWeight: 600,
          color: "#e5c07b",
        }}
      >
        {SITE_NAME}
      </div>
      <div
        style={{
          display: "flex",
          marginTop: 28,
          fontSize: 56,
          fontWeight: 700,
          color: "#ffffff",
          textAlign: "center",
        }}
      >
        {title}
      </div>
      <div
        style={{
          display: "flex",
          marginTop: 16,
          fontSize: 28,
          textAlign: "center",
        }}
      >
        {description}
      </div>

      {/* The moment worth clicking for (Phase AD.9): 9e.3's own analysis already knows the biggest
          clock swing of the game, so the card shows the move and the number rather than only a
          title and a result line. Omitted for a game with no standout swing (see
          replayCardSwing's doc comment), which falls back to the text-only card above. */}
      {swing && (
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 40,
            marginTop: 40,
            padding: "28px 48px",
            borderRadius: 16,
            border: "2px solid #3e4451",
            backgroundColor: "#21252b",
          }}
        >
          <div style={{ display: "flex", flexDirection: "column" }}>
            <div style={{ display: "flex", fontSize: 18, color: "#abb2bf" }}>
              MOVE {swing.ply}
            </div>
            <div
              style={{
                display: "flex",
                marginTop: 4,
                fontSize: 40,
                fontWeight: 700,
                color: "#ffffff",
              }}
            >
              {swing.san}
            </div>
          </div>
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
            }}
          >
            <div
              style={{
                display: "flex",
                fontSize: 60,
                fontWeight: 800,
                color: "#98c379",
              }}
            >
              +{swing.magnitude}s
            </div>
            <div style={{ display: "flex", fontSize: 18, color: "#abb2bf" }}>
              toward {swing.favoredName}
            </div>
          </div>
        </div>
      )}
    </div>,
    { ...size },
  );
}
