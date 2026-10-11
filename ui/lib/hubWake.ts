import * as signalR from "@microsoft/signalr";

/**
 * P-6-5. iOS kills the socket on backgrounding and freezes reconnect timers.
 * When the tab is visible again and SignalR has given up (Disconnected), start
 * the same connection so JoinGame / JoinAsSpectator can run. Connecting and
 * Reconnecting are left alone: an in-flight retry is already the poke.
 */
export async function wakeDisconnectedHub(input: {
  visibilityState: DocumentVisibilityState | string;
  hubState: signalR.HubConnectionState;
  start: () => Promise<void>;
}): Promise<"skipped" | "started" | "failed"> {
  if (input.visibilityState !== "visible") return "skipped";
  if (input.hubState !== signalR.HubConnectionState.Disconnected) {
    return "skipped";
  }
  try {
    await input.start();
    return "started";
  } catch {
    return "failed";
  }
}
