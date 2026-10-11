import { act, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DetectionReportCard } from "./DetectionReportCard";
import type { DetectionReportDto } from "@/lib/types";

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  },
}));

function report(sampleSize: number): DetectionReportDto {
  return {
    userId: "00000000-0000-0000-0000-000000000000",
    displayName: "Player",
    detector: "sandbagging",
    pool: null,
    verdict: "Unremarkable",
    score: 0.1,
    sampleSize,
    signals: [],
    caveats: [],
    computedAt: "2026-01-01T00:00:00Z",
  };
}

describe("DetectionReportCard", () => {
  it("renders a loaded report's sample size after running the analysis", async () => {
    const fetchReports = vi.fn().mockResolvedValue([report(42)]);
    render(
      <DetectionReportCard title="Sandbagging" fetchReports={fetchReports} />,
    );

    await act(async () => {
      screen.getByRole("button", { name: "Run analysis" }).click();
    });

    await waitFor(() =>
      expect(screen.getByText(/from 42 games/)).toBeInTheDocument(),
    );
  });

  it("drops a stale response after the caller re-points this card at a different report (C-10-10)", async () => {
    // A moderator could open a second dossier before the first card's in-flight analysis
    // resolves, without this instance unmounting (the caller re-creates `fetchReports` per
    // render). The card must keep its heading and data in sync with the LATEST fetchReports it
    // was given, never a slower earlier one - mirrors the `cancelled` flag MatchHistoryCard.tsx
    // uses for its own on-mount fetch (C-10-10), adapted to a click-triggered, prop-driven fetch.
    let resolveStale!: (reports: DetectionReportDto[]) => void;
    const stale = new Promise<DetectionReportDto[]>((resolve) => {
      resolveStale = resolve;
    });
    const staleFetch = vi.fn().mockReturnValue(stale);
    const freshFetch = vi.fn().mockResolvedValue([report(99)]);

    const { rerender } = render(
      <DetectionReportCard title="Sandbagging" fetchReports={staleFetch} />,
    );

    await act(async () => {
      screen.getByRole("button", { name: "Run analysis" }).click();
    });
    expect(staleFetch).toHaveBeenCalledTimes(1);

    // The caller moves this same card instance on to a different dossier's fetcher before the
    // first request resolves.
    rerender(
      <DetectionReportCard title="Sandbagging" fetchReports={freshFetch} />,
    );

    await act(async () => {
      resolveStale([report(1)]);
      await Promise.resolve();
    });

    // The stale response must not have written its data in: still no report rendered from it.
    expect(screen.queryByText(/from 1 games/)).toBeNull();
    expect(screen.queryByText(/from 99 games/)).toBeNull();
  });
});
