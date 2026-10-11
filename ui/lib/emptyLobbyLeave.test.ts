import { afterEach, describe, expect, it } from "vitest";
import {
  markGameStarted,
  markQueued,
  resetEmptyLobbyLeave,
  shouldRecordEmptyLobbyLeave,
} from "./emptyLobbyLeave";

afterEach(resetEmptyLobbyLeave);

describe("shouldRecordEmptyLobbyLeave", () => {
  it.each([
    {
      name: "bounce on empty lobby",
      setup: () => {},
      args: { internalNav: false, seekCount: 0, ready: true },
      want: true,
    },
    {
      name: "skip until the first seek poll succeeds",
      setup: () => {},
      args: { internalNav: false, seekCount: 0, ready: false },
      want: false,
    },
    {
      name: "skip if they queued",
      setup: () => markQueued(),
      args: { internalNav: false, seekCount: 0, ready: true },
      want: false,
    },
    {
      name: "skip if they started a bot or solo game",
      setup: () => markGameStarted(),
      args: { internalNav: false, seekCount: 0, ready: true },
      want: false,
    },
    {
      name: "skip if they clicked an in-origin link",
      setup: () => {},
      args: { internalNav: true, seekCount: 0, ready: true },
      want: false,
    },
    {
      name: "skip if seeks are waiting",
      setup: () => {},
      args: { internalNav: false, seekCount: 1, ready: true },
      want: false,
    },
  ])("$name", ({ setup, args, want }) => {
    setup();
    expect(shouldRecordEmptyLobbyLeave(args)).toBe(want);
  });
});
