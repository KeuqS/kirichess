using System.Text.Json;
using System.Text.Json.Serialization;
using KiriChess.Engine.Core;

namespace KiriChess.Simulator;

public sealed class PoolSummary
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Games { get; init; }
    public int Checkmate { get; init; }
    public int FlagFall { get; init; }
    public int Draw { get; init; }
    public int Unterminated { get; init; }
    public double FlagRate { get; init; }
    public double MeanPlies { get; init; }
    public double MeanThinkSecondsPerMove { get; init; }
    public double MeanDurationSeconds { get; init; }
    public double MeanFinalWhite { get; init; }
    public double MeanFinalBlack { get; init; }
    public double MeanTollPaid { get; init; }
    public double MeanCaptureGain { get; init; }
    public IReadOnlyList<GameSummary> GamesDetail { get; init; } = [];
}

public sealed class GameSummary
{
    public int Index { get; init; }
    public int Seed { get; init; }
    public required string Status { get; init; }
    public string? Winner { get; init; }
    public int Plies { get; init; }
    public int ThinkSeconds { get; init; }
    public int FinalWhite { get; init; }
    public int FinalBlack { get; init; }
    public int TollPaid { get; init; }
    public int CaptureGain { get; init; }
    public int CaptureLoss { get; init; }
    public int CheckGain { get; init; }
    public required string Uci { get; init; }
}

public sealed class RunReport
{
    public required int Seed { get; init; }
    public required string Picker { get; init; }
    public int WhiteElo { get; init; }
    public int BlackElo { get; init; }
    public int? FixedThinkMs { get; init; }
    public bool Realtime { get; init; }
    public IReadOnlyList<PoolSummary> Pools { get; init; } = [];
}

public static class ReportBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PoolSummary Summarize(
        string key,
        string label,
        IReadOnlyList<(int Index, SimulatedGame Game)> games)
    {
        static bool IsDraw(GameStatus s) => s is
            GameStatus.Stalemate or
            GameStatus.DrawByRepetition or
            GameStatus.DrawByInsufficientMaterial or
            GameStatus.DrawByFiftyMove;

        var n = games.Count;
        var mate = games.Count(g => g.Game.Status == GameStatus.Checkmate);
        var flag = games.Count(g => g.Game.Status == GameStatus.FlagFall);
        var draw = games.Count(g => IsDraw(g.Game.Status));
        var open = games.Count(g => g.Game.Status is GameStatus.Ongoing or GameStatus.Aborted);
        var plies = games.Sum(g => g.Game.PlyCount);
        var think = games.Sum(g => g.Game.ThinkSecondsTotal);

        return new PoolSummary
        {
            Key = key,
            Label = label,
            Games = n,
            Checkmate = mate,
            FlagFall = flag,
            Draw = draw,
            Unterminated = open,
            FlagRate = n == 0 ? 0 : (double)flag / n,
            MeanPlies = n == 0 ? 0 : games.Average(g => g.Game.PlyCount),
            MeanThinkSecondsPerMove = plies == 0 ? 0 : (double)think / plies,
            MeanDurationSeconds = n == 0 ? 0 : games.Average(g => g.Game.ThinkSecondsTotal),
            MeanFinalWhite = n == 0 ? 0 : games.Average(g => g.Game.Final.WhiteTimeSeconds),
            MeanFinalBlack = n == 0 ? 0 : games.Average(g => g.Game.Final.BlackTimeSeconds),
            MeanTollPaid = n == 0 ? 0 : games.Average(g => g.Game.TollPaid),
            MeanCaptureGain = n == 0 ? 0 : games.Average(g => g.Game.CaptureGain),
            GamesDetail = games.Select(g => new GameSummary
            {
                Index = g.Index,
                Seed = g.Game.Seed,
                Status = g.Game.Status.ToString(),
                Winner = g.Game.Winner?.ToString(),
                Plies = g.Game.PlyCount,
                ThinkSeconds = g.Game.ThinkSecondsTotal,
                FinalWhite = g.Game.Final.WhiteTimeSeconds,
                FinalBlack = g.Game.Final.BlackTimeSeconds,
                TollPaid = g.Game.TollPaid,
                CaptureGain = g.Game.CaptureGain,
                CaptureLoss = g.Game.CaptureLoss,
                CheckGain = g.Game.CheckGain,
                Uci = string.Join(' ', g.Game.Plies.Select(p => p.Uci)),
            }).ToList(),
        };
    }

    public static string FormatTable(IReadOnlyList<PoolSummary> pools)
    {
        var header =
            $"{"pool",-10} {"games",5} {"mate",5} {"flag",5} {"draw",5} {"open",5} " +
            $"{"plies",7} {"think/mv",9} {"duration",9} {"flag%",6}";
        var lines = new List<string> { header, new string('-', header.Length) };
        foreach (var p in pools)
        {
            lines.Add(
                $"{p.Key,-10} {p.Games,5} {p.Checkmate,5} {p.FlagFall,5} {p.Draw,5} {p.Unterminated,5} " +
                $"{p.MeanPlies,7:0.0} {p.MeanThinkSecondsPerMove,8:0.00}s {FormatDuration(p.MeanDurationSeconds),9} {p.FlagRate,5:0%} ");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0.0}s";
        return $"{seconds / 60:0.0}m";
    }
}
