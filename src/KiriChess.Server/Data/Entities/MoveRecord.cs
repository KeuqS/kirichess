using KiriChess.Engine.Core;

namespace KiriChess.Server.Data.Entities;

/// <summary>
/// One ply as stored in the game's move list (serialized to jsonb). Engine-neutral.
///
/// <paramref name="ThinkMs"/> and <paramref name="ReceivedAtUtc"/> are the Phase 6e.2 telemetry: the
/// dataset a future engine-assistance detector runs on, captured from day one because it cannot be
/// reconstructed after the fact. <paramref name="LagMs"/> is Phase 6f.2. All three default so that
/// rows written before their respective phase deserialize cleanly (missing jsonb members become the
/// default) and every existing construction site keeps compiling.
///
/// <c>ThinkMs</c> keeps its original meaning unchanged: measured **server-side, wall clock, receipt
/// to receipt**, so it still includes the mover's full network round-trip. <c>LagMs</c> is the capped,
/// EMA-smoothed round-trip credit that was actually applied to that ply's charged clock, so
/// <c>ThinkMs - LagMs</c> is the corrected think time, net of network distance from the datacentre.
/// Both switching the interval source to monotonic (6f.1) and crediting latency (6f.2) are done; any
/// analysis built on this column can now treat <c>ThinkMs - LagMs</c> as "time the player thought",
/// not merely "time between the server seeing two moves".
/// </summary>
public sealed record MoveRecord(
    int Ply,
    string Uci,
    Side Mover,
    int WhiteTimeAfter,
    int BlackTimeAfter,
    int WhiteDelta,
    int BlackDelta,
    string Status,
    int ThinkMs = 0,
    DateTimeOffset? ReceivedAtUtc = null,
    int LagMs = 0);
