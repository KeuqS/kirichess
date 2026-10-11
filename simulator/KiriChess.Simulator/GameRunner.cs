using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Simulator;

public sealed record GameRunRequest(
    string PoolKey,
    KiriChessConfig Config,
    int WhiteElo,
    int BlackElo,
    int Seed,
    int MaxPlies,
    bool Realtime,
    int? FixedThinkMs);

public sealed record SimulatedPly(
    int Index,
    Side Side,
    string Uci,
    string San,
    int ThinkMs,
    int ThinkSeconds,
    int WhiteTimeAfter,
    int BlackTimeAfter,
    int WhiteDelta,
    int BlackDelta,
    IReadOnlyList<ClockEvent> Events);

public sealed record SimulatedGame(
    string PoolKey,
    int Seed,
    int WhiteElo,
    int BlackElo,
    string WhiteName,
    string BlackName,
    string StartFen,
    string ConfigString,
    int StartTimeSeconds,
    int BaseIncrement,
    IReadOnlyList<SimulatedPly> Plies,
    GamePosition Final,
    GameStatus Status,
    Side? Winner)
{
    public int PlyCount => Plies.Count;
    public int ThinkSecondsWhite => Plies.Where(p => p.Side == Side.White).Sum(p => p.ThinkSeconds);
    public int ThinkSecondsBlack => Plies.Where(p => p.Side == Side.Black).Sum(p => p.ThinkSeconds);
    public int ThinkSecondsTotal => ThinkSecondsWhite + ThinkSecondsBlack;
    public int TollPaid => -Plies.SelectMany(p => p.Events)
        .Where(e => e.Type is ClockEventType.MovementToll or ClockEventType.CastleCost)
        .Sum(e => e.Amount);
    public int CaptureGain => Plies.SelectMany(p => p.Events)
        .Where(e => e.Type == ClockEventType.CaptureGain).Sum(e => e.Amount);
    public int CaptureLoss => -Plies.SelectMany(p => p.Events)
        .Where(e => e.Type == ClockEventType.CaptureLoss).Sum(e => e.Amount);
    public int CheckGain => Plies.SelectMany(p => p.Events)
        .Where(e => e.Type == ClockEventType.CheckGain).Sum(e => e.Amount);
}

/// <summary>
/// Plays one KiriChess game through the real engine. Move quality comes from
/// <see cref="IMoveChooser"/>; think time is charged the way live bots spend it
/// (<see cref="BotClock.SimulatedThinkMs"/>, floored to whole seconds).
/// </summary>
public static class GameRunner
{
    public const int DefaultMaxPlies = 2000;

    public static async Task<SimulatedGame> PlayAsync(
        KiriChessEngine engine,
        GameRunRequest request,
        IMoveChooser chooser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chooser);

        var rng = new Random(request.Seed);
        var config = request.Config;
        var position = engine.NewGame(config);
        var startFen = position.Fen;
        var plies = new List<SimulatedPly>();
        var status = GameStatus.Ongoing;
        Side? winner = null;

        while (plies.Count < request.MaxPlies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var legal = engine.LegalMoves(position);
            if (legal.Count == 0) break;

            // Same reason SelfPlay sorts: Gera.Chess does not promise a stable order, and a seeded
            // random fallback must name the same move on every run.
            var legalUci = legal.Select(m => m.ToString()).OrderBy(u => u, StringComparer.Ordinal).ToList();
            var mover = position.SideToMove;
            var elo = mover == Side.White ? request.WhiteElo : request.BlackElo;
            var remaining = position.TimeFor(mover);

            var thinkMs = request.FixedThinkMs ?? BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(
                RemainingSeconds: remaining,
                LegalMoveCount: legalUci.Count,
                Elo: elo,
                Random: rng.NextDouble));

            var search = chooser.ChooseAsync(position.Fen, elo, legalUci, rng, cancellationToken);
            if (request.Realtime && thinkMs > 0)
                await Task.WhenAll(search, Task.Delay(thinkMs, cancellationToken));

            var uci = await search;
            if (string.IsNullOrWhiteSpace(uci) || !legalUci.Contains(uci, StringComparer.OrdinalIgnoreCase))
                uci = BotSkill.PickRandomMove(legalUci, rng);

            var thinkSeconds = BotClock.ChargedThinkSeconds(thinkMs);
            var result = engine.Apply(position, Move.Parse(uci), config, thinkSeconds);

            var ply = new SimulatedPly(
                plies.Count,
                mover,
                uci,
                string.IsNullOrEmpty(result.San) ? uci : result.San,
                thinkMs,
                thinkSeconds,
                result.NewPosition.WhiteTimeSeconds,
                result.NewPosition.BlackTimeSeconds,
                result.WhiteTimeDelta,
                result.BlackTimeDelta,
                result.Events);
            plies.Add(ply);

            position = result.NewPosition;
            status = result.Status;
            winner = result.Winner;
            if (result.IsGameOver) break;
        }

        return new SimulatedGame(
            request.PoolKey,
            request.Seed,
            request.WhiteElo,
            request.BlackElo,
            chooser.Name,
            chooser.Name,
            startFen,
            config.ToConfigString(),
            config.StartTimeSeconds,
            config.BaseIncrement,
            plies,
            position,
            status,
            winner);
    }

    /// <summary>
    /// Per-game seed. Deliberately not <see cref="HashCode.Combine"/> - that API is randomized per
    /// process, so a seed computed from it would name a different game on every run.
    /// </summary>
    public static int SeedFor(int baseSeed, string poolKey, int gameIndex)
    {
        unchecked
        {
            uint x = (uint)baseSeed;
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
}
