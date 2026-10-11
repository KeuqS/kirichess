using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using KiriChess.Server.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 10a.2, the wiring rather than the arithmetic (<see cref="HubConnectionBudgetTests"/> covers
/// that): a real SignalR client against a real host, proving that live connections are counted, that
/// the slot comes back when one closes, and that a caller over the ceiling is told
/// <see cref="HubConnectionBudget.RefusalMessage"/> by its own <c>StartAsync</c> rather than watching
/// a socket die.
/// <para>
/// <b>What is deliberately not covered.</b> The refusal here comes from the negotiate probe, because
/// a client that has no headroom at negotiate never reaches the filter. Reaching the filter's own
/// refusal needs headroom at negotiate and none a moment later, a race with no deterministic test;
/// what is asserted instead is that the filter runs at all, by reading the counts it maintains.
/// </para>
/// Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class HubConnectionBudgetEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// TestServer's in-memory transport has no remote address, so every client would otherwise key to
    /// <see cref="HubConnectionBudget.UnknownAddress"/>, where the per-IP cap deliberately does not
    /// apply. Sending <c>X-Forwarded-For</c> instead gives each client a real address <em>and</em>
    /// exercises the mechanism the whole item rests on: <c>UseForwardedHeaders()</c> runs first with
    /// the known-network allowlist cleared, so <c>RemoteIpAddress</c> is the caller rather than the
    /// edge by the time anything on the hub sees it.
    /// </summary>
    private const string CallerIp = "203.0.113.7";
    private const string CallerIpKey = "ip:" + CallerIp;
    private const string OtherCallerIp = "198.51.100.4";

    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Connections_are_counted_and_the_one_over_the_cap_is_told_why()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = CreateHost(maxPerIp: 2, maxAnonymous: 100);
        await MigrateAsync(factory);
        var budget = factory.Services.GetRequiredService<HubConnectionBudget>();

        var first = BuildConnection(factory, "hubs/presence");
        var second = BuildConnection(factory, "hubs/presence");
        await first.StartAsync();
        await second.StartAsync();

        // The filter ran on connect: without it nothing would be counted and the cap below could
        // never fire.
        await WaitForCountAsync(budget, CallerIpKey, 2);

        var third = BuildConnection(factory, "hubs/presence");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => third.StartAsync());
        Assert.Contains(HubConnectionBudget.RefusalMessage, refused.Message, StringComparison.Ordinal);

        // A different address is untouched by the first one's exhaustion. This is also the assertion
        // that proves the forwarded header is really reaching RemoteIpAddress: if it were not, both
        // callers would key to the same partition and this would be refused too.
        var elsewhere = BuildConnection(factory, "hubs/presence", ip: OtherCallerIp);
        await elsewhere.StartAsync();
        Assert.Equal(HubConnectionState.Connected, elsewhere.State);
        await elsewhere.DisposeAsync();

        // And the slot comes back, which is what makes "try again shortly" a true statement.
        await first.StopAsync();
        await WaitForCountAsync(budget, CallerIpKey, 1);

        var afterRelease = BuildConnection(factory, "hubs/presence");
        await afterRelease.StartAsync();
        await WaitForCountAsync(budget, CallerIpKey, 2);

        await second.DisposeAsync();
        await third.DisposeAsync();
        await afterRelease.DisposeAsync();
        await first.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_full_house_of_spectators_still_leaves_room_for_a_signed_in_player()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The launch-day shape: the anonymous ceiling is one, the per-IP cap is out of the way, and
        // the question is whether a player can still get a socket while onlookers hold the budget.
        await using var factory = CreateHost(maxPerIp: 100, maxAnonymous: 1);
        await MigrateAsync(factory);

        using var http = factory.CreateClient();
        var token = await RegisterAsync(http, "budget-player@example.com", "Budget Player");

        var budget = factory.Services.GetRequiredService<HubConnectionBudget>();
        var onlooker = BuildConnection(factory, "hubs/presence");
        await onlooker.StartAsync();
        await WaitForCountAsync(budget, CallerIpKey, 1);   // the ceiling is only full once the server says so

        var secondOnlooker = BuildConnection(factory, "hubs/presence");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => secondOnlooker.StartAsync());
        Assert.Contains(HubConnectionBudget.RefusalMessage, refused.Message, StringComparison.Ordinal);

        var player = BuildConnection(factory, "hubs/game", token);
        await player.StartAsync();   // the whole item, in one line

        Assert.Equal(HubConnectionState.Connected, player.State);

        await player.DisposeAsync();
        await onlooker.DisposeAsync();
        await secondOnlooker.DisposeAsync();
    }

    /// <summary>
    /// Neither end of SignalR's connection bookkeeping is synchronous with the client call that
    /// causes it: <c>StartAsync</c> returns once the transport is up, and the server runs
    /// <c>OnConnectedAsync</c> after that, so the count is polled rather than read once. Bounded at
    /// five seconds: a timeout here is a real defect, not flake.
    /// </summary>
    private static async Task WaitForCountAsync(HubConnectionBudget budget, string ipKey, int expected)
    {
        for (var attempt = 0; attempt < 50 && budget.LiveForIp(ipKey) != expected; attempt++)
            await Task.Delay(100);

        Assert.Equal(expected, budget.LiveForIp(ipKey));
    }

    private static HubConnection BuildConnection(
        WebApplicationFactory<Program> factory, string path, string? token = null, string ip = CallerIp) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, path), options =>
            {
                // Long polling over the TestServer handler, same reasoning as TwoInstanceGameTests:
                // the in-memory transport carries no real sockets.
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["X-Forwarded-For"] = ip;
                if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private WebApplicationFactory<Program> CreateHost(int maxPerIp, int maxAnonymous) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                ["Jwt:Key"] = "hub-connection-budget-test-signing-key-at-least-32-bytes",
                ["Jwt:Issuer"] = "kirichess",
                ["Jwt:Audience"] = "kirichess",
                ["Jwt:ExpiryMinutes"] = "60",
                ["Jwt:RefreshTokenExpiryDays"] = "30",
                ["Auth:BreachedPasswordCheckEnabled"] = "false",
                ["Hub:ConnectionBudget:MaxConnectionsPerIp"] = maxPerIp.ToString(),
                ["Hub:ConnectionBudget:MaxAnonymousConnections"] = maxAnonymous.ToString()
            }));
        });

    private static async Task MigrateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();
    }

    private static async Task<string> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return payload.GetProperty("token").GetString()!;
    }
}
