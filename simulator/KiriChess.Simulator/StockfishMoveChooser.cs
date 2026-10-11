using System.Diagnostics;
using System.Text;

namespace KiriChess.Simulator;

/// <summary>
/// One Stockfish process spoken to over UCI. GPL isolation is the same as the site's Worker:
/// Stockfish is a separate program; this file only sends stdin / reads stdout.
/// </summary>
public sealed class StockfishMoveChooser : IMoveChooser
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly object _gate = new();
    private readonly TimeSpan _searchTimeout;
    private bool _disposed;

    public string Name => "Stockfish";

    public StockfishMoveChooser(string executablePath, TimeSpan? searchTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Stockfish path is required.", nameof(executablePath));

        _searchTimeout = searchTimeout ?? TimeSpan.FromSeconds(30);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.ASCII,
            StandardOutputEncoding = Encoding.ASCII,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Stockfish at '{executablePath}'.");

        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;
        _stdout = _process.StandardOutput;

        // Drain stderr so a full pipe cannot stall the engine.
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();

        WaitFor("uci", "uciok", TimeSpan.FromSeconds(10));
        Send("setoption name Threads value 1");
        Send("setoption name Hash value 16");
        WaitFor("isready", "readyok", TimeSpan.FromSeconds(10));
    }

    public Task<string> ChooseAsync(
        string fen,
        int elo,
        IReadOnlyList<string> legalUci,
        Random rng,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (legalUci.Count == 0) return Task.FromResult("");

        var search = BotSkill.ForElo(elo);
        if (search.RandomChance > 0 && rng.NextDouble() < search.RandomChance)
            return Task.FromResult(BotSkill.PickRandomMove(legalUci, rng));

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send($"setoption name Skill Level value {search.SkillLevel}");
            Send($"position fen {fen}");
            var line = WaitFor($"go depth {search.Depth}", "bestmove", _searchTimeout);
            var move = ParseBestMove(line);
            if (move.Length == 0 || !legalUci.Contains(move, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(BotSkill.PickRandomMove(legalUci, rng));
            return Task.FromResult(move);
        }
    }

    public static string ParseBestMove(string line)
    {
        // "bestmove e2e4 ponder e7e5" or "bestmove (none)"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        var move = parts[1];
        if (move.Equals("(none)", StringComparison.OrdinalIgnoreCase)) return "";
        return move.Trim().ToLowerInvariant();
    }

    public static string? Find(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : throw new FileNotFoundException(
                $"Stockfish not found at '{explicitPath}'.", explicitPath);

        var env = Environment.GetEnvironmentVariable("STOCKFISH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (var name in new[] { "stockfish", "stockfish.exe" })
        {
            var found = FindOnPath(name);
            if (found is not null) return found;
        }

        return null;
    }

    public static string InstallHint() =>
        "Stockfish was not found. Install it and put it on PATH, set STOCKFISH to the binary, " +
        "or pass --stockfish <path>. On Windows: winget install --id Stockfish.Stockfish  " +
        "(or download from https://stockfishchess.org/download/). " +
        "To run without it: --picker random.";

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void Send(string command) => _stdin.WriteLine(command);

    private string WaitFor(string command, string terminator, TimeSpan timeout)
    {
        Send(command);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Stockfish exited with {_process.ExitCode} while waiting for '{terminator}'.");

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Stockfish did not emit '{terminator}' within {timeout} after '{command}'.");

            var task = _stdout.ReadLineAsync();
            if (!task.Wait(remaining))
                throw new TimeoutException($"Stockfish stalled waiting for '{terminator}' after '{command}'.");

            var line = task.Result
                ?? throw new EndOfStreamException("Stockfish closed stdout.");
            if (line.StartsWith(terminator, StringComparison.Ordinal)) return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                try { Send("quit"); } catch { /* process may already be gone */ }
                if (!_process.WaitForExit(1000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
        finally
        {
            _stdin.Dispose();
            _stdout.Dispose();
            _process.Dispose();
        }
        await Task.CompletedTask;
    }
}
