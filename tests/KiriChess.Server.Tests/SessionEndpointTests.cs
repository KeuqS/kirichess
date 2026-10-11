using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Phase 9e.6: the active-session list and per-session revoke built on top of 6b.1's refresh-token
/// rows. Requires Docker; skips cleanly when no Docker daemon is reachable, same as
/// <see cref="AuthLifecycleTests"/>.
/// </summary>
public sealed class SessionEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
                    ["Jwt:Key"] = "session-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30",
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
    public async Task Sessions_lists_the_caller_own_session_and_marks_it_current()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("sessions-list-player@example.com", "Sessions List Player");

        var response = await GetSessionsAsync(auth.Token, auth.RefreshToken);
        response.EnsureSuccessStatusCode();
        var sessions = await response.Content.ReadFromJsonAsync<List<SessionRow>>(JsonOptions);

        Assert.NotNull(sessions);
        var mine = Assert.Single(sessions!);
        Assert.True(mine.IsCurrent);
    }

    [SkippableFact]
    public async Task Sessions_grows_by_one_after_a_refresh_and_the_old_row_disappears()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("sessions-rotate-player@example.com", "Sessions Rotate Player");

        var refreshResponse = await _client!.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        refreshResponse.EnsureSuccessStatusCode();
        var rotated = await refreshResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(rotated);

        // Rotation replaces the row, it does not add a second one: RotateAsync revokes the old token
        // in the same operation that issues the new one (see RefreshTokenService.RotateAsync).
        var response = await GetSessionsAsync(rotated!.Token, rotated.RefreshToken);
        response.EnsureSuccessStatusCode();
        var sessions = await response.Content.ReadFromJsonAsync<List<SessionRow>>(JsonOptions);

        Assert.NotNull(sessions);
        var current = Assert.Single(sessions!);
        Assert.True(current.IsCurrent);
    }

    [SkippableFact]
    public async Task Revoking_a_session_by_id_removes_it_and_kills_the_refresh_token()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("sessions-revoke-player@example.com", "Sessions Revoke Player");

        var listResponse = await GetSessionsAsync(auth.Token, auth.RefreshToken);
        listResponse.EnsureSuccessStatusCode();
        var sessions = await listResponse.Content.ReadFromJsonAsync<List<SessionRow>>(JsonOptions);
        var sessionId = Assert.Single(sessions!).Id;

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/account/sessions/{sessionId}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var deleteResponse = await _client!.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // The revoked row's own refresh token is dead now, same contract /api/auth/logout gives.
        var refreshAfterRevoke = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterRevoke.StatusCode);
    }

    [SkippableFact]
    public async Task Revoking_someone_elses_session_id_is_a_silent_no_op()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var victim = await RegisterAsync("sessions-victim-player@example.com", "Sessions Victim Player");
        var attacker = await RegisterAsync("sessions-attacker-player@example.com", "Sessions Attacker Player");

        var victimSessions = await GetSessionsAsync(victim.Token, victim.RefreshToken);
        var victimSessionId = Assert.Single(
            (await victimSessions.Content.ReadFromJsonAsync<List<SessionRow>>(JsonOptions))!).Id;

        // The attacker guesses (or is handed) the victim's session id and tries to revoke it as
        // themselves. RevokeByIdAsync scopes the lookup to the caller's own userId, so this must be a
        // no-op (204, same no-oracle shape as every other revoke route) rather than succeeding.
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/account/sessions/{victimSessionId}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", attacker.Token);
        var deleteResponse = await _client!.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // The victim's own refresh token must still work: the attacker's attempt did nothing.
        var stillWorks = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = victim.RefreshToken }, JsonOptions);
        stillWorks.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Sessions_requires_authentication()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/account/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetSessionsAsync(string accessToken, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Refresh-Token", refreshToken);
        return await _client!.SendAsync(request);
    }

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record SessionRow(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
}
