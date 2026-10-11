using System.Globalization;
using System.Text;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Simulator;

/// <summary>
/// A compact PGN dump for playtesting, not the download contract in <c>docs/game/PGN.md</c>.
/// Movetext is ordinary SAN so Lichess will load it; KiriChess facts sit in comments and extra tags.
/// </summary>
public static class PgnWriter
{
    public static string Build(SimulatedGame game, DateTimeOffset utcNow)
    {
        var sb = new StringBuilder();
        var result = ResultToken(game.Status, game.Winner);
        var label = KiriChessConfig.PoolLabel(game.PoolKey);
        var date = utcNow.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

        AppendTag(sb, "Event", $"KiriChess simulator {label} game");
        AppendTag(sb, "Site", "?");
        AppendTag(sb, "Date", date);
        AppendTag(sb, "Round", "-");
        AppendTag(sb, "White", $"{game.WhiteName} {game.WhiteElo}");
        AppendTag(sb, "Black", $"{game.BlackName} {game.BlackElo}");
        AppendTag(sb, "Result", result);
        AppendTag(sb, "UTCDate", date);
        AppendTag(sb, "UTCTime", utcNow.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        AppendTag(sb, "WhiteElo", game.WhiteElo.ToString(CultureInfo.InvariantCulture));
        AppendTag(sb, "BlackElo", game.BlackElo.ToString(CultureInfo.InvariantCulture));
        AppendTag(sb, "TimeControl", $"{game.StartTimeSeconds}+{game.BaseIncrement}");
        AppendTag(sb, "Termination", TerminationTag(game.Status));
        AppendTag(sb, "KiriChessPool", game.PoolKey);
        AppendTag(sb, "KiriChessRated", "false");
        AppendTag(sb, "KiriChessTermination", game.Status.ToString());
        sb.Append('\n');

        var tokens = new List<string> { $"{{ [%kcfg {game.ConfigString}] }}" };
        var moveNumber = 1;
        var black = false;
        foreach (var ply in game.Plies)
        {
            tokens.Add(black ? $"{moveNumber}..." : $"{moveNumber}.");
            tokens.Add(ply.San);
            tokens.Add(
                $"{{ [%clk {FormatClock(black ? ply.BlackTimeAfter : ply.WhiteTimeAfter)}] " +
                $"[%kc {ply.WhiteTimeAfter},{ply.BlackTimeAfter},{ply.WhiteDelta},{ply.BlackDelta}] " +
                $"[%emt {FormatClock(ply.ThinkSeconds)}] }}");
            if (black) moveNumber++;
            black = !black;
        }
        tokens.Add(result);
        sb.Append(WrapTokens(tokens));
        sb.Append('\n').Append('\n');
        return sb.ToString();
    }

    public static string ResultToken(GameStatus status, Side? winner) =>
        (status, winner) switch
        {
            (_, Side.White) => "1-0",
            (_, Side.Black) => "0-1",
            (GameStatus.Ongoing or GameStatus.Aborted, _) => "*",
            _ => "1/2-1/2",
        };

    public static string TerminationTag(GameStatus status) => status switch
    {
        GameStatus.FlagFall => "Time forfeit",
        GameStatus.Aborted => "Abandoned",
        GameStatus.Ongoing => "Unterminated",
        _ => "Normal",
    };

    private static void AppendTag(StringBuilder sb, string name, string value) =>
        sb.Append('[').Append(name).Append(" \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"]\n");

    private static string FormatClock(int seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string WrapTokens(IReadOnlyList<string> tokens)
    {
        var sb = new StringBuilder();
        var lineLength = 0;
        foreach (var token in tokens)
        {
            if (lineLength == 0)
            {
                sb.Append(token);
                lineLength = token.Length;
            }
            else if (lineLength + 1 + token.Length <= 80)
            {
                sb.Append(' ').Append(token);
                lineLength += 1 + token.Length;
            }
            else
            {
                sb.Append('\n').Append(token);
                lineLength = token.Length;
            }
        }
        return sb.ToString();
    }
}
