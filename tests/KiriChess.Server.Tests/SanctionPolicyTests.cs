using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// <see cref="SanctionPolicy.BlockingSanctionAsync"/> against a real Postgres: every enforcement branch
/// the login/refresh/queue-join/game-create checks depend on (Phase 8.5). Requires Docker; skips
/// cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class SanctionPolicyTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;

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
                    ["Jwt:Key"] = "sanction-policy-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task No_sanctions_blocks_neither_scope()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("no-sanctions@example.com");
        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.RatedPlay));
    }

    [SkippableFact]
    public async Task Active_account_ban_blocks_both_scopes()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("account-ban@example.com");
        var issuerId = await CreateUserAsync("account-ban-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.AccountBan, issuerId);

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.NotNull(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
        Assert.NotNull(await policy.BlockingSanctionAsync(userId, SanctionScope.RatedPlay));
    }

    [SkippableFact]
    public async Task Active_rated_play_ban_blocks_rated_play_but_not_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("rated-play-ban@example.com");
        var issuerId = await CreateUserAsync("rated-play-ban-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.RatedPlayBan, issuerId);

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
        Assert.NotNull(await policy.BlockingSanctionAsync(userId, SanctionScope.RatedPlay));
    }

    [SkippableFact]
    public async Task Warning_blocks_neither_scope()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The one that catches an over-eager `Type != Warning` refactor.
        var userId = await CreateUserAsync("warning-only@example.com");
        var issuerId = await CreateUserAsync("warning-only-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.Warning, issuerId);

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.RatedPlay));
    }

    [SkippableFact]
    public async Task Expired_sanction_does_not_block()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("expired-ban@example.com");
        var issuerId = await CreateUserAsync("expired-ban-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.AccountBan, issuerId,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
    }

    [SkippableFact]
    public async Task Lifted_sanction_does_not_block_even_with_a_future_expiry()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("lifted-ban@example.com");
        var issuerId = await CreateUserAsync("lifted-ban-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.AccountBan, issuerId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            liftedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();

        Assert.Null(await policy.BlockingSanctionAsync(userId, SanctionScope.Account));
    }

    [SkippableFact]
    public async Task Permanent_ban_outranks_a_temporary_one()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var userId = await CreateUserAsync("both-bans@example.com");
        var issuerId = await CreateUserAsync("both-bans-issuer@example.com");
        await AddSanctionAsync(userId, SanctionType.AccountBan, issuerId,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7));
        await AddSanctionAsync(userId, SanctionType.AccountBan, issuerId, expiresAt: null);

        var policy = _factory!.Services.GetRequiredService<SanctionPolicy>();
        var blocking = await policy.BlockingSanctionAsync(userId, SanctionScope.Account);

        Assert.NotNull(blocking);
        Assert.Null(blocking!.ExpiresAt);
    }

    private async Task<Guid> CreateUserAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user);
        Assert.True(result.Succeeded);
        return user.Id;
    }

    private async Task AddSanctionAsync(
        Guid userId, SanctionType type, Guid issuedByUserId,
        DateTimeOffset? expiresAt = null, DateTimeOffset? liftedAt = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.UserSanctions.Add(new UserSanction
        {
            UserId = userId,
            Type = type,
            Reason = "Test sanction.",
            IssuedByUserId = issuedByUserId,
            ExpiresAt = expiresAt,
            LiftedAt = liftedAt,
            LiftedByUserId = liftedAt is not null ? issuedByUserId : null,
            LiftReason = liftedAt is not null ? "Lifted for test." : null
        });
        await db.SaveChangesAsync();
    }
}
