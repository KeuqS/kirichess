import { describe, expect, it } from "vitest";
import en from "../messages/en.json";
import type { Translator } from "./tutorial";
import {
  describeClocks,
  describeMove,
  describePromotionPrompt,
  describeSelection,
  describeSquare,
  describeTarget,
  crossedThreshold,
  spokenTime,
  URGENCY_THRESHOLDS,
} from "./a11y";
import type { ClockEventDto } from "./types";

function lookup(messages: unknown, key: string): string {
  let cur: unknown = messages;
  for (const part of key.split(".")) {
    cur = (cur as Record<string, unknown>)[part];
  }
  if (typeof cur !== "string") throw new Error(`missing a11y key ${key}`);
  return cur;
}

/** Reads `messages/en.json` `a11y` the way production's next-intl translator does, without pulling next-intl into the node test project. */
const t: Translator = (key, values) => {
  let s = lookup(en.a11y, key);
  if (!values) return s;
  s = s.replace(
    /\{(\w+), plural, one \{([^}]*)\} other \{([^}]*)\}\}/g,
    (_, name: string, one: string, other: string) => {
      const n = Number(values[name]);
      return (n === 1 ? one : other).replaceAll("#", String(n));
    },
  );
  for (const [k, v] of Object.entries(values)) {
    s = s.replaceAll(`{${k}}`, String(v));
  }
  return s;
};

describe("describeSquare", () => {
  const fen = "4k3/8/8/8/8/8/P7/4K3 w - - 0 1";

  it("names an occupied square with its colour and piece", () => {
    expect(describeSquare(fen, "a2", t)).toBe("a2, white pawn");
  });

  it("names an empty square as empty", () => {
    expect(describeSquare(fen, "a3", t)).toBe("a3, empty");
  });

  it("names a black piece", () => {
    expect(describeSquare(fen, "e8", t)).toBe("e8, black king");
  });
});

describe("describeTarget", () => {
  // White pawn e4, black pawn d5: a capture is available.
  const fen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1";

  it("appends the price when the mover pays (negative cost)", () => {
    expect(describeTarget(fen, "e4", "d5", -2, t)).toBe(
      "d5, black pawn, capture, costs you 2 seconds",
    );
  });

  it("appends the price when the mover gains (positive cost)", () => {
    expect(describeTarget(fen, "e4", "d5", 4, t)).toBe(
      "d5, black pawn, capture, gains you 4 seconds",
    );
  });

  it("uses singular 'second' for a price of exactly 1", () => {
    expect(describeTarget(fen, "e4", "d5", -1, t)).toBe(
      "d5, black pawn, capture, costs you 1 second",
    );
  });

  it("drops the price clause entirely when cost is 0, never says 'costs 0 seconds'", () => {
    expect(describeTarget(fen, "e4", "d5", 0, t)).toBe(
      "d5, black pawn, capture",
    );
  });

  it("drops the price clause when cost is undefined (no economy loaded yet)", () => {
    expect(describeTarget(fen, "e4", "d5", undefined, t)).toBe(
      "d5, black pawn, capture",
    );
  });

  it("omits 'capture' for a quiet target square", () => {
    expect(describeTarget(fen, "e4", "e5", -1, t)).toBe(
      "e5, empty, costs you 1 second",
    );
  });
});

describe("describeSelection", () => {
  it("names the square and a singular target count", () => {
    expect(describeSelection("e2", 1, t)).toBe("e2 selected, 1 legal target.");
  });

  it("pluralises two or more targets", () => {
    expect(describeSelection("e2", 2, t)).toBe("e2 selected, 2 legal targets.");
  });

  it("says no legal targets rather than '0 legal targets'", () => {
    expect(describeSelection("e1", 0, t)).toBe("e1 selected, no legal targets.");
  });
});

describe("describePromotionPrompt", () => {
  it("lists every offered piece with its key, queen first", () => {
    expect(describePromotionPrompt("a8", ["q", "n", "b", "r"], t)).toBe(
      "Promote on a8: press Q for queen, N for knight, B for bishop, or R for rook.",
    );
  });

  it("orders by key even if the caller passed a different sequence", () => {
    expect(describePromotionPrompt("e8", ["r", "q"], t)).toBe(
      "Promote on e8: press Q for queen, or R for rook.",
    );
  });

  it("drops the 'or' when only one piece is legal", () => {
    expect(describePromotionPrompt("a8", ["n"], t)).toBe(
      "Promote on a8: press N for knight.",
    );
  });
});

describe("describeMove: quiet move", () => {
  it("names the piece and destination, no economy clause, no check", () => {
    const result = describeMove({
      priorFen: "4k3/8/8/8/8/8/P7/4K3 w - - 0 1",
      uci: "a2a3",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn to a3.");
  });
});

describe("describeMove: capture with a gain and a loss in the same events array", () => {
  const priorFen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1"; // Rule1CaptureEconomyTests.PawnTradeFen

  it("one utterance in canonical order: move, your delta, opponent's delta, check", () => {
    const events: ClockEventDto[] = [
      { type: "CaptureGain", side: "White", amount: 5, detail: null },
      { type: "CaptureLoss", side: "Black", amount: -5, detail: null },
    ];
    const result = describeMove({
      priorFen,
      uci: "e4d5",
      events,
      you: "White",
      mover: "White",
      check: true,
      t,
    });
    expect(result).toBe(
      "Pawn takes d5 pawn. You plus 5, opponent minus 5. Check.",
    );
  });

  it("reads from the opponent's side symmetrically", () => {
    const events: ClockEventDto[] = [
      { type: "CaptureGain", side: "White", amount: 5, detail: null },
      { type: "CaptureLoss", side: "Black", amount: -5, detail: null },
    ];
    const result = describeMove({
      priorFen,
      uci: "e4d5",
      events,
      you: "Black",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn takes d5 pawn. You minus 5, opponent plus 5.");
  });
});

describe("describeMove: capture where one side's net is zero", () => {
  const priorFen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1";

  it("drops the zero clause instead of saying 'plus 0'", () => {
    // Bounty economy: only the mover gains, the victim's clock is untouched, so no CaptureLoss
    // event is ever raised for the other side.
    const events: ClockEventDto[] = [
      { type: "CaptureGain", side: "White", amount: 5, detail: null },
    ];
    const result = describeMove({
      priorFen,
      uci: "e4d5",
      events,
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn takes d5 pawn. You plus 5.");
  });

  it("drops the whole economy clause when both nets are zero", () => {
    const result = describeMove({
      priorFen,
      uci: "e4d5",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn takes d5 pawn.");
  });
});

describe("describeMove: castling", () => {
  it("kingside", () => {
    const result = describeMove({
      priorFen: "4k3/8/8/8/8/8/8/4K2R w K - 0 1",
      uci: "e1g1",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Castles kingside.");
  });

  it("queenside", () => {
    const result = describeMove({
      priorFen: "4k3/8/8/8/8/8/8/R3K3 w Q - 0 1",
      uci: "e1c1",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Castles queenside.");
  });
});

describe("describeMove: promotion", () => {
  it("names the destination and the promoted-to piece from the fifth UCI character", () => {
    const result = describeMove({
      priorFen: "4k3/P7/8/8/8/8/8/4K3 w - - 0 1",
      uci: "a7a8q",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn to a8, promotes to queen.");
  });

  it("reads the promoted piece, not always queen", () => {
    const result = describeMove({
      priorFen: "4k3/P7/8/8/8/8/8/4K3 w - - 0 1",
      uci: "a7a8n",
      events: [],
      you: "White",
      mover: "White",
      t,
    });
    expect(result).toBe("Pawn to a8, promotes to knight.");
  });
});

describe("spokenTime", () => {
  it("0 seconds", () => {
    expect(spokenTime(0, t)).toBe("0 seconds");
  });

  it("59 seconds stays under a minute", () => {
    expect(spokenTime(59, t)).toBe("59 seconds");
  });

  it("60 seconds is exactly 1 minute, no '0 seconds' tail", () => {
    expect(spokenTime(60, t)).toBe("1 minute");
  });

  it("92 seconds is 1 minute 32 seconds", () => {
    expect(spokenTime(92, t)).toBe("1 minute 32 seconds");
  });

  it("3600 seconds is 60 minutes", () => {
    expect(spokenTime(3600, t)).toBe("60 minutes");
  });

  it("never renders 'M:SS'", () => {
    expect(spokenTime(92, t)).not.toContain(":");
  });
});

describe("describeClocks: gap phrasing", () => {
  it("ahead", () => {
    expect(describeClocks(100, 80, t)).toBe(
      "You 1 minute 40 seconds, opponent 1 minute 20 seconds. You are ahead by 20 seconds.",
    );
  });

  it("behind", () => {
    expect(describeClocks(80, 100, t)).toBe(
      "You 1 minute 20 seconds, opponent 1 minute 40 seconds. You are behind by 20 seconds.",
    );
  });

  it("level", () => {
    expect(describeClocks(90, 90, t)).toBe(
      "You 1 minute 30 seconds, opponent 1 minute 30 seconds. You are level.",
    );
  });
});

describe("URGENCY_THRESHOLDS", () => {
  it("is 60, 30, 15, 10 then every second from 5 down, per design decision 3", () => {
    expect(URGENCY_THRESHOLDS).toEqual([60, 30, 15, 10, 5, 4, 3, 2, 1]);
  });
});

describe("crossedThreshold", () => {
  it("reports every threshold when the clock falls through it", () => {
    for (const t of URGENCY_THRESHOLDS) {
      expect(crossedThreshold(t + 0.5, t - 0.1)).toBe(t);
    }
  });

  it("reports a crossing that lands exactly on the threshold", () => {
    expect(crossedThreshold(30.4, 30)).toBe(30);
  });

  it("returns null while the clock stays between two thresholds", () => {
    expect(crossedThreshold(45.9, 45.2)).toBeNull();
    expect(crossedThreshold(20.1, 16.0)).toBeNull();
  });

  it("returns null above the highest threshold", () => {
    expect(crossedThreshold(300, 200)).toBeNull();
    expect(crossedThreshold(90, 61)).toBeNull();
  });

  it("catches a threshold even when one tick swallows it whole", () => {
    // The reason this function takes a pair. A 250ms ticker that stalls (backgrounded tab, GC
    // pause) can jump 30.4 straight to 29.2 without ever reading a value inside the 30th second.
    expect(crossedThreshold(30.4, 29.2)).toBe(30);
  });

  it("reports the most urgent threshold when one gap crosses several", () => {
    // 12 -> 4 crosses 10, 5 and 4. Only "4 seconds" is true when it is spoken; announcing the
    // highest crossed would tell a player in time trouble a number they are already past.
    expect(crossedThreshold(12, 4)).toBe(4);
    expect(crossedThreshold(61, 0.5)).toBe(1);
  });

  it("returns null when the clock is rising, since this variant gives time back", () => {
    expect(crossedThreshold(9, 35)).toBeNull();
  });

  it("announces again on a second crossing, with no re-arming needed", () => {
    expect(crossedThreshold(31, 29)).toBe(30);
    // Time won back, then spent again: the pair alone makes this a fresh crossing.
    expect(crossedThreshold(29, 40)).toBeNull();
    expect(crossedThreshold(40, 29)).toBe(30);
  });
});
