namespace KiriChess.Server.Games;

/// <summary>
/// Latency-compensation policy (Phase 6f.2). The server stamps a move's clock when it *processes*
/// the move, so without compensation every player is charged their own network round-trip on every
/// ply, ~15s over a 60-move game at a 250ms transatlantic RTT, on a clock that might only be 60
/// seconds long. Credit is measured server-side (never client-reported), smoothed, and capped.
/// </summary>
public sealed class LagCompensationOptions
{
    public const string SectionName = "Clock";

    /// <summary>
    /// Master switch. Off means the probe never runs and no interval is ever credited, the exact
    /// pre-6f.2 behaviour. Defaults on: unlike the Sentry DSN or the metrics token, this needs no
    /// external account, so there is nothing to leave inert.
    /// </summary>
    public bool LagCompensationEnabled { get; set; } = true;

    /// <summary>
    /// Hard ceiling on the credit given to any one ply, in milliseconds. Covers real
    /// intercontinental RTT (~250–350ms) with headroom while bounding what a client stalling its
    /// probe reply can steal, and even that shows up afterwards in <c>MoveRecord.LagMs</c>.
    /// </summary>
    public int MaxCompensationMs { get; set; } = 500;

    /// <summary>How often each live connection is probed, in seconds.</summary>
    public int ProbeIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// How long a single probe may take before it is abandoned with no measurement recorded. A
    /// timeout must never be treated as "this player has enormous latency", an unanswered probe
    /// means the client did not answer, not that the network is slow.
    /// </summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// EMA weight on the newest sample (0–1). Lower reacts more slowly and is more resistant to a
    /// client deliberately stalling one reply.
    /// </summary>
    public double EmaAlpha { get; set; } = 0.3;
}
