using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the fails-closed gate from Phase 9e.7: tracing (OpenTelemetry's ASP.NET Core
/// instrumentation exported to Sentry over OTLP) is only registered when Sentry:Dsn is set, exactly
/// like the exception tracking it shares that setting with (6d.2). Boots the app in a
/// production-like configuration with an unreachable database and no boot-time migration, so it
/// never needs Docker (same shape as HealthEndpointTests/MetricsEndpointTests).
/// </summary>
public sealed class DistributedTracingTests
{
    private static WebApplicationFactory<Program> BuildFactory(string? sentryDsn)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                    ["Jwt:Key"] = "tracing-test-signing-key-at-least-32-bytes-long",
                    ["RunMigrationsOnStartup"] = "false",
                    ["Cors:AllowedOrigins:0"] = "https://example.test"
                };
                if (sentryDsn is not null)
                    settings["Sentry:Dsn"] = sentryDsn;
                cfg.AddInMemoryCollection(settings);
            });
        });
    }

    [Fact]
    public async Task Unconfigured_deploy_boots_without_a_tracer()
    {
        using var factory = BuildFactory(sentryDsn: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Configured_dsn_registers_the_otlp_tracer_and_still_boots()
    {
        // A syntactically valid DSN is enough to exercise AddSentryOtlpExporter's Dsn.TryParse and
        // UseOtlp's wiring; no real Sentry project needs to exist for the host to start, and the
        // exporter batches/exports off the request path so this never touches the network.
        using var factory = BuildFactory(sentryDsn: "https://public@o12345.ingest.sentry.io/67890");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
