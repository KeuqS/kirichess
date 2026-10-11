using System.Net;
using System.Net.Http.Headers;
using KiriChess.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the "fails closed" contract from Phase 6d: the Prometheus scrape
/// endpoint must not exist at all unless a gate is configured, and the token gate must reject
/// anything but the exact configured bearer token. Boots the app in a production-like configuration
/// with an unreachable database and no boot-time migration, so it never needs Docker (same shape as
/// HealthEndpointTests). Note the Production environment also means JSON console logging is active
/// in these tests, that is fine and is itself evidence the sink does not crash the host.
/// </summary>
public sealed class MetricsEndpointTests
{
    private static WebApplicationFactory<Program> BuildFactory(string? scrapeToken, int? metricsPort = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                    ["Jwt:Key"] = "metrics-test-signing-key-at-least-32-bytes-long",
                    ["RunMigrationsOnStartup"] = "false",
                    ["Cors:AllowedOrigins:0"] = "https://example.test"
                };
                if (scrapeToken is not null)
                    settings["Metrics:ScrapeToken"] = scrapeToken;
                if (metricsPort is not null)
                    settings["Metrics:Port"] = metricsPort.Value.ToString();
                cfg.AddInMemoryCollection(settings);
            });
        });
    }

    [Fact]
    public async Task Unconfigured_deploy_returns_404()
    {
        using var factory = BuildFactory(scrapeToken: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_bearer_token_returns_401()
    {
        using var factory = BuildFactory("test-scrape-token");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_bearer_token_returns_401()
    {
        using var factory = BuildFactory("test-scrape-token");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Correct_bearer_token_returns_200_with_prometheus_text()
    {
        using var factory = BuildFactory("test-scrape-token");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-scrape-token");

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The Prometheus exporter mangles `kirichess.games.live` to `kirichess_games_live` and appends
        // unit/type suffixes, assert on substrings, not exact metric names. Only the observable
        // gauges are asserted: a counter with no recorded increment (e.g. games.started, in a test
        // that never created a game) is not emitted at all.
        Assert.Contains("kirichess_games_live", body);
        Assert.Contains("kirichess_presence_online_users", body);
    }

    /// <summary>
    /// The regression that cost 9c.5 its metrics half. Production sets both gates
    /// (<c>Metrics__Port = 9091</c> in fly.backend.toml plus a scrape token), and the guard used to
    /// treat them as a conjunction: a request on the published port was 404'd before the token was
    /// ever looked at, so an external scraper holding the correct token got a bare 404 forever while
    /// <c>/health</c> on the same machine returned 200.
    /// </summary>
    [Fact]
    public async Task Correct_bearer_token_returns_200_even_when_a_metrics_port_is_configured()
    {
        using var factory = BuildFactory("test-scrape-token", metricsPort: 9091);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-scrape-token");

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The other half of that change: relaxing the port gate must not turn a port-only deploy into a
    /// public one. With no token there is nothing the public port could ever check, so it stays 404.
    /// </summary>
    [Fact]
    public async Task Metrics_port_without_a_token_stays_404_on_the_public_port()
    {
        using var factory = BuildFactory(scrapeToken: null, metricsPort: 9091);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// The guard's decision matrix, unit-tested directly. The integration tests above cannot reach the
/// private-network case at all: TestServer synthesizes a connection with no real socket behind it,
/// so <c>LocalPort</c> is 0 and never matches a configured <c>Metrics:Port</c>. That case, Fly's
/// built-in Prometheus scraping 9091 with no bearer token, is the one whose regression would be
/// invisible until a deploy, so it is asserted here instead of not at all.
/// </summary>
public sealed class MetricsScrapeGuardTests
{
    private const int MetricsPort = 9091;
    private const int PublicPort = 8080;

    [Fact]
    public void Private_metrics_port_is_allowed_without_a_token()
    {
        var options = new MetricsOptions(MetricsPort, "secret");

        Assert.Equal(MetricsAccess.Allow, MetricsEndpoint.Evaluate(options, MetricsPort, ""));
    }

    [Fact]
    public void Private_metrics_port_is_allowed_when_no_token_is_configured_at_all()
    {
        var options = new MetricsOptions(MetricsPort, null);

        Assert.Equal(MetricsAccess.Allow, MetricsEndpoint.Evaluate(options, MetricsPort, ""));
    }

    [Fact]
    public void Public_port_with_the_correct_token_is_allowed()
    {
        var options = new MetricsOptions(MetricsPort, "secret");

        Assert.Equal(MetricsAccess.Allow, MetricsEndpoint.Evaluate(options, PublicPort, "Bearer secret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("secret")]              // token without the scheme
    [InlineData("bearer secret")]       // scheme casing is not negotiable here
    public void Public_port_without_the_exact_token_is_unauthorized(string header)
    {
        var options = new MetricsOptions(MetricsPort, "secret");

        Assert.Equal(MetricsAccess.Unauthorized, MetricsEndpoint.Evaluate(options, PublicPort, header));
    }

    [Fact]
    public void Public_port_with_no_token_configured_is_not_found()
    {
        var options = new MetricsOptions(MetricsPort, null);

        Assert.Equal(MetricsAccess.NotFound, MetricsEndpoint.Evaluate(options, PublicPort, "Bearer anything"));
    }

    /// <summary>
    /// Token-only deploys (no <c>Metrics:Port</c>, which is every local run and every test above)
    /// must gate on the token alone rather than falling into the private-network branch.
    /// </summary>
    [Fact]
    public void Token_only_deploy_gates_on_the_token_regardless_of_port()
    {
        var options = new MetricsOptions(0, "secret");

        Assert.Equal(MetricsAccess.Allow, MetricsEndpoint.Evaluate(options, PublicPort, "Bearer secret"));
        Assert.Equal(MetricsAccess.Unauthorized, MetricsEndpoint.Evaluate(options, PublicPort, ""));
        Assert.Equal(MetricsAccess.Unauthorized, MetricsEndpoint.Evaluate(options, 0, ""));
    }
}
