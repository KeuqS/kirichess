using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the liveness/readiness contract from Phase 6a Boots the app in a
/// production-like configuration with an unreachable database and no boot-time migration, so it
/// never needs Docker.
/// </summary>
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Points at a port nothing is listening on: readiness must report 503, not throw.
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                ["Jwt:Key"] = "health-test-signing-key-at-least-32-bytes-long",
                ["RunMigrationsOnStartup"] = "false",
                ["Cors:AllowedOrigins:0"] = "https://example.test"
            }));
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Health_returns_200_ok()
    {
        var response = await _client!.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusBody>();
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_database_unreachable()
    {
        var response = await _client!.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Factory_boots_with_migrations_disabled_and_no_database()
    {
        // Regression test for A4(b): the host must start (and respond) even when
        // RunMigrationsOnStartup=false and the database is unreachable.
        var response = await _client!.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record StatusBody(string Status);
}
