using KiriChess.Server.Infrastructure.OpenApi;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 10c.5. 9c.5's Terraform watches the app. This item's Terraform is the three signals that
/// are not the app: the Cloudflare-proxied hostnames, a heartbeat the synthetic must ping, and a
/// heartbeat the Neon watch must ping. A deleted resource here is the same failure
/// <see cref="CiWorkflowDriftTests"/> exists for - a claimed control that silently un-ships.
/// </summary>
public sealed class BetterStackAlertingDriftTests
{
    private static string RepoRoot => OpenApiExport.FindRepoRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    [Fact]
    public void Terraform_declares_the_three_10c5_signals()
    {
        var terraform = Read("infra", "betterstack", "main.tf");

        Assert.Contains("resource \"betteruptime_monitor\" \"edge_api\"", terraform);
        Assert.Contains("resource \"betteruptime_monitor\" \"edge_site\"", terraform);
        Assert.Contains("resource \"betteruptime_heartbeat\" \"synthetic_play\"", terraform);
        Assert.Contains("resource \"betteruptime_heartbeat\" \"neon_storage\"", terraform);

        Assert.Contains("var.edge_api_url", terraform);
        Assert.Contains("expected_status_codes = [200]", terraform);
        Assert.DoesNotContain("expected_status_codes = [200, 302]", terraform);
    }

    [Fact]
    public void Synthetic_play_script_still_asserts_the_clock_moved()
    {
        var script = Read("scripts", "synthetic-play.sh");

        Assert.Contains("/api/v1/games", script);
        Assert.Contains("e2e4", script);
        Assert.Contains("whiteDelta", script);
        Assert.Contains("whiteTime", script);
        Assert.Contains("SYNTHETIC_HEARTBEAT_URL", script);
        Assert.Contains("api.kirichess.com", script);
    }

    [Fact]
    public void Neon_watch_script_still_reads_synthetic_storage_size()
    {
        var script = Read("scripts", "neon-watch.sh");

        Assert.Contains("synthetic_storage_size", script);
        Assert.Contains("/endpoints", script);
        Assert.Contains("NEON_WATCH_HEARTBEAT_URL", script);
        Assert.Contains("read_write", script);
    }
}
