using System.Diagnostics;
using System.Text;
using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Guards the public-snapshot path: this working repo's git authors include personal
/// addresses, so a later public repo must be a clean tree, not a push of this history.
/// These checks look at tracked file contents only - they do not rewrite history.
/// Needles are assembled at runtime so this file does not itself contain a mailbox.
/// </summary>
public sealed class TrackedTreeHygieneTests
{
    private static readonly string[] Forbidden = BuildForbidden();

    private static string[] BuildForbidden() =>
    [
        "@" + "gmail" + ".com",
        "@student." + "howest" + ".be",
        "@" + "howest" + ".be",
        "." + "(none)",
    ];

    [Fact]
    public void Mailmap_is_gitignored()
    {
        var gitignore = File.ReadAllText(Path.Combine(OpenApiExport.FindRepoRoot(), ".gitignore"));
        Assert.Contains(".mailmap", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrangler_cache_is_gitignored()
    {
        var gitignore = File.ReadAllText(Path.Combine(OpenApiExport.FindRepoRoot(), ".gitignore"));
        Assert.Contains(".wrangler/", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracked_files_do_not_contain_personal_mailbox_addresses()
    {
        var root = OpenApiExport.FindRepoRoot();
        var hits = new List<string>();

        foreach (var relative in GitLsFiles(root))
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;

            var bytes = File.ReadAllBytes(path);
            if (bytes.IndexOf((byte)0) >= 0)
                continue;

            var text = Encoding.UTF8.GetString(bytes);
            foreach (var needle in Forbidden)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{relative}: {needle}");
            }
        }

        Assert.True(
            hits.Count == 0,
            "Personal mailbox addresses must not be in tracked files. They leak the moment a "
            + "public snapshot copies the tree. Hits:\n" + string.Join('\n', hits));
    }

    private static IEnumerable<string> GitLsFiles(string root)
    {
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("git ls-files failed to start.");
        var stdout = process.StandardOutput.BaseStream;
        using var reader = new StreamReader(stdout, Encoding.UTF8);
        var blob = reader.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-files exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }

        return blob.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
