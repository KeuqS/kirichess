using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests.Generative;

/// <summary>
/// The shared driver for KiriChess's generative engine tests (Phase 10b.2): a reproducible
/// random-legal self-play walk, plus the reporting and budget plumbing every property test in this
/// folder uses.
///
/// <para><b>Why the move list is sorted before the RNG indexes it.</b>
/// <see cref="KiriChessEngine.LegalMoves"/> does not promise a stable order - Gera.Chess parallelises
/// generation, and <c>EngineThroughputTests.LegalMoveOrderIsNotStableButResultsAre</c> pins that as a
/// known property rather than a bug. Indexing an unsorted list with a seeded RNG therefore produces a
/// <i>different game on every run</i>, which would make every failure in this folder irreproducible -
/// the one thing that turns a generative suite into noise. <see cref="Walk"/> sorts by UCI first, so a
/// seed plus a pool key names exactly one game, for good.</para>
/// </summary>
internal static class SelfPlay
{
    /// <summary>
    /// Fixed base seed. Changing it re-rolls every game this suite plays, so change it only
    /// deliberately (e.g. to go hunting), never to make a failing test pass.
    /// </summary>
    public const int BaseSeed = 20260818;

    /// <summary>
    /// Hard ply ceiling per game. Measured: random self-play under the shipped pools terminates on
    /// its own well inside this (longest observed game, across all six pools, was 464 plies), so
    /// hitting the ceiling means the engine stopped adjudicating and is itself worth failing on.
    /// </summary>
    public const int MaxPlies = 2000;

    /// <summary>
    /// The per-game seed for a (pool, game index) pair. Deliberately not <see cref="HashCode.Combine"/>:
    /// that API is randomized per process (to resist hash-flooding), so a seed computed from it would
    /// name a different game on every test run and make every failure irreproducible. String
    /// <see cref="string.GetHashCode()"/> has the same caveat, so the pool key is mixed with FNV-1a
    /// rather than the runtime's hash.
    /// </summary>
    public static int SeedFor(string poolKey, int gameIndex)
    {
        unchecked
        {
            uint x = (uint)BaseSeed;
            foreach (var c in poolKey)
            {
                x ^= c;
                x *= 0x01000193;
            }
            x ^= (uint)gameIndex;
            x *= 0x01000193;
            x ^= x >> 16;
            x *= 0x85ebca6b;
            x ^= x >> 13;
            x *= 0xc2b2ae35;
            x ^= x >> 16;
            return (int)x;
        }
    }

    /// <summary>
    /// Plays one random-legal game, calling <paramref name="inspect"/> once per ply <b>before</b>
    /// advancing, and returns the finished game. Every invariant test in this folder is an
    /// <paramref name="inspect"/> body; none of them should re-implement the walk.
    /// </summary>
    /// <param name="thinkSeconds">
    /// Per-ply think time to charge the mover. Defaults to 0 (pure economy). Pass a non-zero picker to
    /// exercise flag-fall paths.
    /// </param>
    public static SelfPlayGame Walk(
        KiriChessEngine engine,
        KiriChessConfig config,
        string poolKey,
        int seed,
        Action<Ply>? inspect = null,
        Func<Random, int>? thinkSeconds = null)
    {
        var rng = new Random(seed);
        var position = engine.NewGame(config);
        var startFen = position.Fen;
        var played = new List<string>();
        var status = GameStatus.Ongoing;
        Side? winner = null;

        while (played.Count < MaxPlies)
        {
            var legal = engine.LegalMoves(position);
            if (legal.Count == 0)
            {
                // Not a "no moves, so stop" convenience: a position the engine still calls Ongoing
                // must have a move. Surfacing it as a game-level fact lets the caller assert on it.
                break;
            }

            // See the class remarks: sort, or the seed means nothing.
            var ordered = legal.OrderBy(m => m.ToString(), StringComparer.Ordinal).ToList();
            var move = ordered[rng.Next(ordered.Count)];
            var think = thinkSeconds?.Invoke(rng) ?? 0;

            var before = position;
            var result = engine.Apply(before, move, config, think);

            inspect?.Invoke(new Ply(
                played.Count,
                before,
                ordered,
                move,
                result,
                config,
                poolKey,
                seed,
                played,
                think));

            played.Add(move.ToString());
            position = result.NewPosition;
            status = result.Status;
            winner = result.Winner;

            if (result.IsGameOver) break;
        }

        return new SelfPlayGame(poolKey, seed, startFen, config.ToConfigString(), played, position, status, winner);
    }

    /// <summary>Every shipped rated/unrated pool, by key. The suite runs against all of them because
    /// the rules are config-driven: an invariant that holds only for <c>steal1</c> is not an
    /// invariant of KiriChess.</summary>
    public static IReadOnlyList<string> PoolKeys => KiriChessConfig.PresetOrder;

    /// <summary>
    /// The config for a pool. Always a <see cref="KiriChessConfig.Clone"/>: <c>KiriChessConfig.Presets</c>
    /// hands out shared master copies, and a test that mutates one would silently re-tune every later
    /// test in the run.
    /// </summary>
    public static KiriChessConfig ConfigFor(string poolKey) => KiriChessConfig.Presets[poolKey].Clone();
}

/// <summary>One ply of a self-play walk, as handed to an invariant.</summary>
/// <param name="Index">0-based ply number within the game.</param>
/// <param name="Before">The position the move was applied to. Immutable - asserting it is unchanged after <see cref="Result"/> is itself one of the invariants.</param>
/// <param name="Legal">All legal moves in <paramref name="Before"/>, sorted by UCI.</param>
/// <param name="Move">The move that was chosen.</param>
/// <param name="Result">What the engine returned.</param>
/// <param name="MovesSoFar">UCI of every ply before this one - the reproduction for a failure here.</param>
internal sealed record Ply(
    int Index,
    GamePosition Before,
    IReadOnlyList<Move> Legal,
    Move Move,
    ApplyResult Result,
    KiriChessConfig Config,
    string PoolKey,
    int Seed,
    IReadOnlyList<string> MovesSoFar,
    int ThinkSeconds);

/// <summary>A finished self-play game, reproducible from <see cref="Seed"/> alone.</summary>
internal sealed record SelfPlayGame(
    string PoolKey,
    int Seed,
    string StartFen,
    string ConfigString,
    IReadOnlyList<string> Uci,
    GamePosition Final,
    GameStatus Status,
    Side? Winner);

/// <summary>
/// Collects invariant violations across a whole run and renders one failure message that is enough to
/// reproduce any of them, rather than throwing on the first and hiding the shape of the problem.
///
/// <para>A generative test that fails with "iteration 43917 failed" and nothing else costs more than
/// it earns; every entry here carries pool, seed, ply and the full move list, so a finding can be
/// pasted straight into an example-based regression test.</para>
/// </summary>
internal sealed class InvariantLedger
{
    private const int MaxPerInvariant = 3;
    private readonly List<string> _entries = new();
    private readonly Dictionary<string, int> _seen = new();
    private long _checks;

    /// <summary>How many invariant checks were actually performed - guards against a test that
    /// passes because its loop never ran.</summary>
    public long Checks => _checks;

    public void Checked(long count = 1) => _checks += count;

    /// <summary>Records a violation, keeping at most <see cref="MaxPerInvariant"/> per invariant name.</summary>
    public void Violation(string invariant, Ply ply, string detail)
    {
        _checks++;
        var count = _seen.GetValueOrDefault(invariant);
        _seen[invariant] = count + 1;
        if (count >= MaxPerInvariant) return;

        _entries.Add(
            $"[{invariant}] {detail}" + Environment.NewLine +
            $"    pool={ply.PoolKey} seed={ply.Seed} ply={ply.Index} move={ply.Move} think={ply.ThinkSeconds}" + Environment.NewLine +
            $"    fenBefore={ply.Before.Fen}" + Environment.NewLine +
            $"    config={ply.Config.ToConfigString()}" + Environment.NewLine +
            $"    moves={string.Join(" ", ply.MovesSoFar)} {ply.Move}");
    }

    /// <summary>Records a violation not tied to a single ply (e.g. a whole-game fact).</summary>
    public void Violation(string invariant, string detail)
    {
        _checks++;
        var count = _seen.GetValueOrDefault(invariant);
        _seen[invariant] = count + 1;
        if (count >= MaxPerInvariant) return;
        _entries.Add($"[{invariant}] {detail}");
    }

    /// <summary>Fails the test if anything was recorded, and fails it too if nothing was ever checked.</summary>
    public void AssertClean(string what)
    {
        Assert.True(_checks > 0, $"{what}: the generative walk performed no checks at all.");
        if (_entries.Count == 0) return;

        var summary = string.Join(", ", _seen.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}"));
        Assert.Fail(
            $"{what}: {_seen.Sum(k => k.Value)} invariant violation(s) across {_checks:N0} checks ({summary})." +
            Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, _entries));
    }
}

/// <summary>
/// How much work the generative suite does. CI gets a bounded run; a soak run is opt-in via
/// environment variable, which is how 9b.3's "no rung ran longer than 180s" gap gets explored without
/// putting minutes onto every pull request.
///
/// <para><c>KIRICHESS_ENGINE_SOAK=1</c> multiplies the game counts and adds a perft depth.
/// <c>KIRICHESS_ENGINE_SOAK_FACTOR=&lt;n&gt;</c> overrides the multiplier (default 25).</para>
/// </summary>
internal static class GenerativeBudget
{
    public static bool IsSoak =>
        Environment.GetEnvironmentVariable("KIRICHESS_ENGINE_SOAK") is "1" or "true" or "TRUE";

    public static int Factor =>
        int.TryParse(Environment.GetEnvironmentVariable("KIRICHESS_ENGINE_SOAK_FACTOR"), out var f) && f > 0
            ? f
            : 25;

    /// <summary>Games per pool: <paramref name="normal"/> on CI, scaled up in a soak run.</summary>
    public static int Games(int normal) => IsSoak ? normal * Factor : normal;

    /// <summary>Extra perft depth to add to each position's CI depth when soaking.</summary>
    public static int PerftDepthBonus => IsSoak ? 1 : 0;
}
