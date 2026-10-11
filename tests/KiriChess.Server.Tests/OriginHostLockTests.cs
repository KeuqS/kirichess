using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.OpenApi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// I-2-2: the public Fly hostname must not serve the API. Probes Better Stack and Fly already aim
/// at that hostname stay up. Decision tests here; production wiring is the fly.backend.toml assertion.
/// </summary>
public sealed class OriginHostLockTests
{
    private static readonly string[] ProductionBlocked = ["kirichess-api.fly.dev"];
    private static readonly string[] ProductionAllowed = ["api.kirichess.com"];

    [Fact]
    public void Empty_list_never_rejects()
    {
        Assert.False(OriginHostLock.ShouldReject(
            new HostString("kirichess-api.fly.dev"), "/api/auth/login", []));
    }

    [Fact]
    public void Custom_domain_is_not_blocked()
    {
        Assert.False(OriginHostLock.ShouldReject(
            new HostString("api.kirichess.com"), "/api/auth/login", ProductionBlocked));
    }

    [Fact]
    public void Fly_hostname_rejects_api_paths()
    {
        Assert.True(OriginHostLock.ShouldReject(
            new HostString("kirichess-api.fly.dev"), "/api/auth/login", ProductionBlocked));
        Assert.True(OriginHostLock.ShouldReject(
            new HostString("kirichess-api.fly.dev"), "/hubs/game", ProductionBlocked));
    }

    [Fact]
    public void Fly_hostname_is_matched_case_insensitively_and_ignores_port()
    {
        Assert.True(OriginHostLock.ShouldReject(
            new HostString("KIRICHESS-API.FLY.DEV:443"), "/api/auth/login", ProductionBlocked));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public void Fly_hostname_keeps_probe_paths(string path)
    {
        Assert.False(OriginHostLock.ShouldReject(
            new HostString("kirichess-api.fly.dev"), path, ProductionBlocked));
    }

    [Fact]
    public void Allow_list_accepts_the_public_api_host_and_rejects_a_spoofed_one()
    {
        Assert.Null(OriginHostLock.RejectionStatus(
            new HostString("api.kirichess.com"), "/api/auth/login", ProductionBlocked, ProductionAllowed));
        Assert.Null(OriginHostLock.RejectionStatus(
            new HostString("API.KIRICHESS.COM:443"), "/hubs/game", ProductionBlocked, ProductionAllowed));
        Assert.Equal(StatusCodes.Status400BadRequest, OriginHostLock.RejectionStatus(
            new HostString("evil.example"), "/api/auth/login", ProductionBlocked, ProductionAllowed));
    }

    [Fact]
    public void Blocked_host_stays_404_when_an_allow_list_is_also_set()
    {
        Assert.Equal(StatusCodes.Status404NotFound, OriginHostLock.RejectionStatus(
            new HostString("kirichess-api.fly.dev"), "/api/auth/login", ProductionBlocked, ProductionAllowed));
    }

    [Fact]
    public void Allow_list_still_serves_probes_on_a_private_address()
    {
        // Fly's metrics scrape arrives with the machine's 6PN address as Host. That address cannot
        // be written into a static list, which is why this is not ASP.NET AllowedHosts.
        var privateHost = new HostString("[fdaa:0:1:a7b:123:456:7890:1]:9091");
        Assert.Null(OriginHostLock.RejectionStatus(
            privateHost, "/metrics", ProductionBlocked, ProductionAllowed));
        Assert.Null(OriginHostLock.RejectionStatus(
            privateHost, "/health/ready", ProductionBlocked, ProductionAllowed));
    }

    [Fact]
    public void Empty_allow_list_does_not_reject_an_unknown_host()
    {
        Assert.Null(OriginHostLock.RejectionStatus(
            new HostString("localhost"), "/api/auth/login", [], []));
        Assert.False(OriginHostLock.ShouldReject(
            new HostString("localhost"), "/api/auth/login", ProductionBlocked));
    }

    [Fact]
    public void Missing_host_fails_closed_only_when_an_allow_list_is_set()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, OriginHostLock.RejectionStatus(
            new HostString(string.Empty), "/api/auth/login", ProductionBlocked, ProductionAllowed));
        Assert.Null(OriginHostLock.RejectionStatus(
            new HostString(string.Empty), "/api/auth/login", ProductionBlocked, []));
    }

    [Fact]
    public void Production_fly_toml_blocks_the_public_fly_hostname_and_staging_does_not()
    {
        var root = OpenApiExport.FindRepoRoot();
        var prod = File.ReadAllText(Path.Combine(root, "fly.backend.toml"));
        var staging = File.ReadAllText(Path.Combine(root, "fly.backend.staging.toml"));

        Assert.Contains("OriginLock__BlockedHosts__0", prod, StringComparison.Ordinal);
        Assert.Contains("kirichess-api.fly.dev", prod, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginLock__BlockedHosts", staging, StringComparison.Ordinal);

        Assert.Contains("OriginLock__AllowedHosts__0 = \"api.kirichess.com\"", prod, StringComparison.Ordinal);
        Assert.Contains(
            "OriginLock__AllowedHosts__0 = \"kirichess-api-staging.fly.dev\"",
            staging,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedHosts =", prod, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedHosts =", staging, StringComparison.Ordinal);
    }
}
