using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// A controllable <see cref="IGameClock"/> for tests (Phase 6f.1). <see cref="Advance"/> moves both
/// domains together, which is what a healthy clock does. <see cref="StepWallClock"/> moves *only*
/// the wall clock, simulating the NTP correction / VM migration / host clock sync that 6f.1 exists
/// to be immune to, after one of those, elapsed time must be unchanged.
/// </summary>
public sealed class FakeGameClock : IGameClock
{
    // An arbitrary non-zero origin: a monotonic source has no meaningful epoch, and starting at 0
    // would let a "forgot to anchor" bug accidentally pass.
    private long _ticks = 1_000_000;

    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public long Timestamp => _ticks;

    public TimeSpan Elapsed(long sinceTimestamp) =>
        TimeSpan.FromTicks(Math.Max(0, _ticks - sinceTimestamp));

    /// <summary>Time passes normally: both the wall clock and the monotonic counter move forward.</summary>
    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        UtcNow = UtcNow.Add(by);
    }

    /// <summary>
    /// The wall clock jumps without any real time passing, an NTP step. Negative values are a
    /// backward correction, which is the direction that used to hand a player free time.
    /// </summary>
    public void StepWallClock(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
