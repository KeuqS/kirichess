using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the AD.6+11a.3 fold of the analytics surface onto <c>ModerationPolicies.Admin</c>: the
/// routes are now always mapped (the 8.3 "404 when unconfigured" contract is gone with the static
/// token) and answer 401/403 through the same JWT + role middleware every other admin route uses.
/// Boots the app in a production-like configuration with an unreachable database and no boot-time
/// migration, so it never needs Docker (same shape as MetricsEndpointTests): every case here is
/// decided before the database is ever touched.
/// </summary>
public sealed class AnalyticsEndpointTests
{
    private static WebApplicationFactory<Program> BuildFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                    ["Jwt:Key"] = "analytics-test-signing-key-at-least-32-bytes-long",
                    ["RunMigrationsOnStartup"] = "false",
                    ["Cors:AllowedOrigins:0"] = "https://example.test"
                });
            });
        });
    }

    [Theory]
    [InlineData("/api/analytics/balance")]
    [InlineData("/api/analytics/retention")]
    public async Task Anonymous_gets_401_not_404(string path)
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        // The deliberate reversal of 8.3's fail-closed 404: the static token that made this route
        // conditional is gone, so the group is always mapped and an anonymous caller meets the JWT
        // middleware (401), never a missing route (404).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/analytics/balance")]
    [InlineData("/api/analytics/retention")]
    public async Task Garbage_bearer_token_gets_401_not_404(string path)
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        var response = await client.GetAsync(path);

        // An unsigned/nonsense JWT must not be waved through, and - the interesting failure mode after
        // this change - a 401 from the JWT middleware must not be confused with a 404 from a group that
        // was never mapped.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
