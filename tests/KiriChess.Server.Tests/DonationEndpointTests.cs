using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// HTTP coverage of the donation-reference mint. Requires Docker; skips cleanly when no daemon is
/// reachable. The consume race lives in <see cref="PatronServiceTests"/>; this is the reuse half of
/// $1-4 (do not mint a second row while one is already live).
/// </summary>
public sealed class DonationEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "donation-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Auth:BreachedPasswordCheckEnabled"] = "false"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Minting_twice_reuses_the_unconsumed_reference()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var register = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "mint-reuse@example.com",
            Password,
            DisplayName = "Mint Reuse",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var first = await _client.PostAsync("/api/donations/reference", null);
        first.EnsureSuccessStatusCode();
        var minted = await first.Content.ReadFromJsonAsync<DonationReferenceDto>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(minted?.Reference));

        var second = await _client.PostAsync("/api/donations/reference", null);
        second.EnsureSuccessStatusCode();
        var reused = await second.Content.ReadFromJsonAsync<DonationReferenceDto>(JsonOptions);

        Assert.Equal(minted!.Reference, reused!.Reference);

        using var scope = _factory!.Services.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>()
            .DonationReferences.CountAsync(r => r.UserId == auth.UserId);
        Assert.Equal(1, count);
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);
}
