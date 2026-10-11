namespace KiriChess.Server.Tests;

/// <summary>Probes whether a local Docker daemon is reachable, so DB-backed tests can skip cleanly.</summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> _available = new(Probe);
    public static bool IsAvailable => _available.Value;

    private static bool Probe()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
