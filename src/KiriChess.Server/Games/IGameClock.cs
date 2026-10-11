using System.Diagnostics;

namespace KiriChess.Server.Games;

/// <summary>
/// The two kinds of time a live game needs, deliberately kept apart (Phase 6f.1).
///
/// <see cref="UtcNow"/> is a **wall clock**: the right and only source for instants that get
/// persisted or displayed (<c>CreatedAt</c>, <c>EndedAt</c>, <c>ReceivedAtUtc</c>), they have to
/// mean something across process and machine boundaries, and only a wall clock does.
///
/// <see cref="Timestamp"/> is a **monotonic** tick counter with no defined epoch. It is meaningless
/// on its own and only ever used as the left-hand side of <see cref="Elapsed"/>. It exists because
/// a wall clock can *step*, NTP correction, VM live-migration, host clock sync, `date -s`, and
/// subtracting two readings of a steppable clock is not a valid way to measure an interval. In
/// KiriChess that is not a rounding error: think time is charged straight off that interval, so a
/// +2s NTP step deducts two real seconds from whoever is on move and can flag them outright.
///
/// Rule of thumb for anything added later: **wall clock for instants, monotonic for intervals.**
/// </summary>
public interface IGameClock
{
    /// <summary>Current wall-clock time. For persisted/displayed instants only, never for intervals.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// A monotonic tick reading with no meaningful epoch. Store it, then pass it to
    /// <see cref="Elapsed"/> later. Never subtract two of these by hand, the tick frequency is
    /// platform-defined, which is exactly the mistake <see cref="Elapsed"/> exists to prevent.
    /// </summary>
    long Timestamp { get; }

    /// <summary>Time elapsed since a reading previously taken from <see cref="Timestamp"/>.</summary>
    TimeSpan Elapsed(long sinceTimestamp);
}

/// <summary>
/// The production clock: <see cref="DateTimeOffset.UtcNow"/> for instants,
/// <see cref="Stopwatch.GetTimestamp"/> (monotonic) for intervals. Stateless, so one shared
/// instance is correct and nothing needs to dispose it.
/// </summary>
public sealed class SystemGameClock : IGameClock
{
    /// <summary>The shared instance. <see cref="GameSession"/> falls back to this when none is injected.</summary>
    public static readonly SystemGameClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long Timestamp => Stopwatch.GetTimestamp();

    public TimeSpan Elapsed(long sinceTimestamp) => Stopwatch.GetElapsedTime(sinceTimestamp);
}
