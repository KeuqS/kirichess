import * as signalR from "@microsoft/signalr";
import { API_BASE, getAuthToken } from "./api";

/**
 * Stops `conn`, but not before an in-flight `start()` has settled.
 *
 * `HubConnection.stop()` called while `start()` is still negotiating aborts the negotiation, and
 * SignalR reports it as "Failed to start the connection: Error: The connection was stopped during
 * negotiation." That message comes out of SignalR's own logger inside `start()`, so a `.catch()` on
 * the start promise silences the unhandled rejection but never the console line.
 *
 * React StrictMode runs every effect as mount/cleanup/mount in dev, so the natural shape (start in
 * the effect body, stop in the cleanup) hits that race on every mount of every component holding a
 * hub connection. It is not only dev noise: the same race fires in production whenever a visitor
 * navigates away inside the negotiation window. Awaiting the start first removes both, and stopping
 * an already-failed connection is a no-op.
 *
 * Pass the promise returned by `conn.start()`, not a re-invocation.
 */
export function stopWhenSettled(
  conn: signalR.HubConnection,
  started: Promise<unknown>,
  beforeStop?: () => void | Promise<void>,
): void {
  void started
    .catch(() => {})
    .then(async () => {
      if (beforeStop && conn.state === signalR.HubConnectionState.Connected) {
        try {
          await beforeStop();
        } catch {
          // Best-effort courtesy call on the way out; the stop below is what matters.
        }
      }
      await conn.stop();
    });
}

/**
 * P-6-5. Longer than SignalR's default `[0, 2000, 10000, 30000]` (~42s, then
 * onclose). Seven attempts, last delay 60s. After the last entry SignalR
 * stops, which is when P-4-2's closed dialog still has work to do.
 */
export const GAME_HUB_RECONNECT_DELAYS_MS = [
  0, 2000, 5000, 10000, 30000, 30000, 60000,
] as const;

/**
 * Creates (but does not start) a hub connection to the game hub, authenticated with the current token.
 *
 * Also the spectator transport (Phase 8.1): with no token the factory returns "" and SignalR omits
 * the credential, so the connection arrives anonymous and can invoke `JoinAsSpectator`: the one hub
 * method that does not require authorization. Every player method rejects it.
 */
export function createGameConnection(): signalR.HubConnection {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/game`, {
      accessTokenFactory: () => getAuthToken() ?? "",
    })
    .withAutomaticReconnect([...GAME_HUB_RECONNECT_DELAYS_MS])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  // Phase 6f.2: answer the server's latency probe. This is a SignalR *client result*: the server
  // calls this and times the round-trip against its own clock, which is why the handler must return
  // immediately and why the returned value is irrelevant (the timing is the measurement). Nothing
  // here is trusted as a clock reading: the client never tells the server what time it is.
  connection.on("Ping", () => 1);

  return connection;
}

/**
 * Creates (but does not start) a connection to the presence hub, which pushes the live lobby counts.
 * Anonymous-friendly: with no token the visitor still receives the counts, they just aren't in them
 * (SignalR omits the credential entirely when the factory returns an empty string).
 */
export function createPresenceConnection(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/presence`, {
      accessTokenFactory: () => getAuthToken() ?? "",
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}
