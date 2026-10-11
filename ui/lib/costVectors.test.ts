import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { computeCostPreview } from "./costPreview";
import type { PieceChar, Square } from "./fen";
import type { EconomyConfig } from "./types";

/**
 * Roadmap 10b.1: config/cost-vectors.generated.json is a checked-in set of golden
 * (position, move) -> economy vectors, generated from the C# engine
 * (src/KiriChess.Engine/Tooling/CostVectorGenerator.cs, run via
 * `dotnet run --project src/KiriChess.Server -- --export-cost-vectors config/cost-vectors.generated.json`).
 * tests/KiriChess.Engine.Tests/CostVectorConformanceTests.cs guards that the committed file is fresh
 * relative to KiriChessEngine.ApplyEconomy; this file is the other half - it replays every vector
 * through computeCostPreview and asserts the TypeScript mirror agrees.
 */
interface CostVector {
  label: string;
  fen: string;
  from: string;
  to: string;
  promotion: PieceChar | null;
  economy: EconomyConfig;
  expected: {
    moverNet: number;
    capture: boolean;
    victimType: PieceChar | null;
    opponentLoss: number;
    hasCheckChip: boolean;
  };
}

const here = path.dirname(fileURLToPath(import.meta.url));
const fixturePath = path.resolve(
  here,
  "../../config/cost-vectors.generated.json",
);
const fixture = JSON.parse(readFileSync(fixturePath, "utf8")) as {
  vectors: CostVector[];
};

describe("computeCostPreview matches the engine's cost vectors", () => {
  it("loaded a nonempty, generated fixture", () => {
    expect(fixture.vectors.length).toBeGreaterThan(0);
  });

  for (const vector of fixture.vectors) {
    it(vector.label, () => {
      const result = computeCostPreview(
        vector.fen,
        vector.from as Square,
        vector.to as Square,
        vector.economy,
        vector.promotion ?? "q",
      );

      expect(result).toEqual(vector.expected);
    });
  }
});
