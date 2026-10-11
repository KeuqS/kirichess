using System.Diagnostics.Metrics;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Retention;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// <see cref="RetentionSweep"/> against a real Postgres. Requires Docker; skips cleanly when no
/// Docker daemon is reachable. Fixture scaffolding is copied verbatim from
/// <see cref="SanctionPolicyTests"/>. RetentionSweep is built by hand rather than resolved from the
/// factory, since each test needs its own RetentionOptions grace values.
///
/// The rotated-but-unexpired-token test is the whole point of this file: README.md ground truth 2
/// says a rotated RefreshToken row is a theft tripwire, and it must survive a sweep for as long as
/// its own ExpiresAt says it should, no matter how long ago it was revoked.
/// </summary>
public sealed class RetentionSweepTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

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
                    ["Jwt:Key"] = "retention-sweep-test-signing-key-at-least-32-bytes",
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
    public async Task Expired_refresh_token_past_the_grace_is_deleted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "expired-past-grace");
        var tokenId = await AddRefreshTokenAsync(db, userId, expiresAt: Now.AddDays(-31));

        var result = await RunSweepAsync(db, refreshTokenGraceDays: 30);

        Assert.False(await db.RefreshTokens.AnyAsync(t => t.Id == tokenId));
        Assert.Equal(1, result.Rules.Single(r => r.Name == RetentionRules.RefreshTokens).RowsDeleted);
    }

    [SkippableFact]
    public async Task Expired_refresh_token_inside_the_grace_survives()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "expired-inside-grace");
        var tokenId = await AddRefreshTokenAsync(db, userId, expiresAt: Now.AddDays(-29));

        await RunSweepAsync(db, refreshTokenGraceDays: 30);

        Assert.True(await db.RefreshTokens.AnyAsync(t => t.Id == tokenId));
    }

    [SkippableFact]
    public async Task Rotated_token_row_survives_while_its_own_expiry_is_in_the_future()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // README ground truth 2: RefreshTokenService.RotateAsync tells a replayed stolen token from
        // an unknown one only by finding a row whose RevokedAt is already set. Purging by RevokedAt
        // instead of ExpiresAt would delete this row and silently disarm that check for good tokens
        // that simply rotated a long time ago.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "rotated-long-ago");
        var tokenId = await AddRefreshTokenAsync(
            db, userId, expiresAt: Now.AddDays(1), revokedAt: Now.AddDays(-60));

        await RunSweepAsync(db, refreshTokenGraceDays: 30);

        Assert.True(await db.RefreshTokens.AnyAsync(t => t.Id == tokenId));
    }

    [SkippableFact]
    public async Task Boundary_is_exclusive_a_row_expiring_exactly_on_the_cutoff_is_not_deleted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "on-the-boundary");
        // grace 30d, now = Now => cutoff = Now - 30d. ExpiresAt set exactly to the cutoff must
        // survive: the comparison is strictly "<", never "<=".
        var tokenId = await AddRefreshTokenAsync(db, userId, expiresAt: Now.AddDays(-30));

        await RunSweepAsync(db, refreshTokenGraceDays: 30);

        Assert.True(await db.RefreshTokens.AnyAsync(t => t.Id == tokenId));
    }

    [SkippableFact]
    public async Task External_login_code_past_its_grace_is_deleted_a_live_or_consumed_one_is_not()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "external-codes");

        var expiredId = await AddExternalLoginCodeAsync(db, userId, expiresAt: Now.AddDays(-2));
        var liveId = await AddExternalLoginCodeAsync(db, userId, expiresAt: Now.AddMinutes(1));
        // A consumed code inside the grace still survives - unlike a rotated refresh token, a spent
        // code carries no detection value, but the grace window is what protects it either way.
        var consumedInsideGraceId = await AddExternalLoginCodeAsync(
            db, userId, expiresAt: Now.AddHours(-1), consumedAt: Now.AddHours(-1));

        await RunSweepAsync(db, externalCodeGraceDays: 1);

        Assert.False(await db.ExternalLoginCodes.AnyAsync(c => c.Id == expiredId));
        Assert.True(await db.ExternalLoginCodes.AnyAsync(c => c.Id == liveId));
        Assert.True(await db.ExternalLoginCodes.AnyAsync(c => c.Id == consumedInsideGraceId));
    }

    [SkippableFact]
    public async Task Result_reports_per_rule_counts_and_a_second_sweep_deletes_nothing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "idempotent-sweep");
        await AddRefreshTokenAsync(db, userId, expiresAt: Now.AddDays(-31));
        await AddExternalLoginCodeAsync(db, userId, expiresAt: Now.AddDays(-2));
        await AddTwoFactorChallengeAsync(db, userId, expiresAt: Now.AddDays(-2));

        var first = await RunSweepAsync(db, refreshTokenGraceDays: 30, externalCodeGraceDays: 1, twoFactorChallengeGraceDays: 1);
        Assert.Equal(1, first.Rules.Single(r => r.Name == RetentionRules.RefreshTokens).RowsDeleted);
        Assert.Equal(1, first.Rules.Single(r => r.Name == RetentionRules.ExternalLoginCodes).RowsDeleted);
        Assert.Equal(1, first.Rules.Single(r => r.Name == RetentionRules.TwoFactorChallenges).RowsDeleted);
        Assert.Equal(0, first.Rules.Single(r => r.Name == RetentionRules.ApiTokens).RowsDeleted);
        Assert.Equal(3, first.TotalRowsDeleted);

        var second = await RunSweepAsync(db, refreshTokenGraceDays: 30, externalCodeGraceDays: 1, twoFactorChallengeGraceDays: 1);
        Assert.Equal(0, second.TotalRowsDeleted);
    }

    [SkippableFact]
    public async Task Two_factor_challenge_past_its_grace_is_deleted_a_live_or_consumed_one_is_not()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "two-factor-challenges");

        var expiredId = await AddTwoFactorChallengeAsync(db, userId, expiresAt: Now.AddDays(-2));
        var liveId = await AddTwoFactorChallengeAsync(db, userId, expiresAt: Now.AddMinutes(1));
        // A consumed ticket inside the grace still survives - same reasoning as the external-login-code
        // case: a spent row carries no detection value, but the grace window protects it either way.
        var consumedInsideGraceId = await AddTwoFactorChallengeAsync(
            db, userId, expiresAt: Now.AddHours(-1), consumedAt: Now.AddHours(-1));

        await RunSweepAsync(db, twoFactorChallengeGraceDays: 1);

        Assert.False(await db.TwoFactorChallenges.AnyAsync(c => c.Id == expiredId));
        Assert.True(await db.TwoFactorChallenges.AnyAsync(c => c.Id == liveId));
        Assert.True(await db.TwoFactorChallenges.AnyAsync(c => c.Id == consumedInsideGraceId));
    }

    [SkippableFact]
    public async Task Revoked_or_expired_api_token_past_grace_is_deleted_and_a_null_expiry_survives()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(db, "api-tokens");

        var revokedId = await AddApiTokenAsync(db, userId, revokedAt: Now.AddDays(-31));
        var expiredId = await AddApiTokenAsync(db, userId, expiresAt: Now.AddDays(-31));
        // S-1-4 no longer mints a null ExpiresAt. The sweep still cannot measure a grace from one,
        // so a leftover null stays until it is revoked or the account is erased.
        var activeId = await AddApiTokenAsync(db, userId);

        await RunSweepAsync(db, apiTokenGraceDays: 30);

        Assert.False(await db.ApiTokens.AnyAsync(t => t.Id == revokedId));
        Assert.False(await db.ApiTokens.AnyAsync(t => t.Id == expiredId));
        Assert.True(await db.ApiTokens.AnyAsync(t => t.Id == activeId));
    }

    [SkippableFact]
    public async Task Ban_evasion_marker_survives_while_the_ban_is_active_and_is_cleared_past_the_grace()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // S-3-1's storage-limitation half. The refusal itself is a query-time condition on the
        // sanction row (BanEvasionTests proves that), so nothing waits on this sweep to be correct;
        // what the sweep guarantees is that the identifier does not outlive the ban it enforces.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        // A permanent, unlifted ban has no end to measure a grace from, so its marker is kept.
        var permanent = await CreateMarkedTombstoneAsync(db, "marker-permanent");
        await AddAccountBanAsync(db, permanent);

        // Lifted long ago: past the grace, so the marker goes.
        var liftedLongAgo = await CreateMarkedTombstoneAsync(db, "marker-lifted-old");
        await AddAccountBanAsync(db, liftedLongAgo, liftedAt: Now.AddDays(-31));

        // Lifted yesterday: inside the grace, so the marker stays even though it already refuses
        // nothing. The window is what makes a mistaken lift reversible.
        var liftedRecently = await CreateMarkedTombstoneAsync(db, "marker-lifted-new");
        await AddAccountBanAsync(db, liftedRecently, liftedAt: Now.AddDays(-1));

        // Expired long ago: same outcome as lifted long ago, by the other route.
        var expiredLongAgo = await CreateMarkedTombstoneAsync(db, "marker-expired-old");
        await AddAccountBanAsync(db, expiredLongAgo, expiresAt: Now.AddDays(-31));

        var result = await RunSweepAsync(db, banEvasionMarkerGraceDays: 30);

        Assert.Equal(2, result.Rules.Single(r => r.Name == RetentionRules.BanEvasionMarkers).RowsDeleted);
        Assert.NotNull(await MarkerOfAsync(db, permanent));
        Assert.Null(await MarkerOfAsync(db, liftedLongAgo));
        Assert.NotNull(await MarkerOfAsync(db, liftedRecently));
        Assert.Null(await MarkerOfAsync(db, expiredLongAgo));

        // The sanction rows themselves are never touched by any of this - they are the appeal path.
        Assert.Equal(4, await db.UserSanctions.CountAsync(s => s.Type == SanctionType.AccountBan));
    }

    private static async Task<Guid> CreateMarkedTombstoneAsync(KiriChessDbContext db, string tag)
    {
        var userId = await CreateUserAsync(db, tag);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.DeletedAt = Now.AddDays(-60);
        user.Email = null;
        user.NormalizedEmail = null;
        user.BanEvasionEmailHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, 'a')[..64];
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task AddAccountBanAsync(
        KiriChessDbContext db, Guid userId, DateTimeOffset? liftedAt = null, DateTimeOffset? expiresAt = null)
    {
        db.UserSanctions.Add(new UserSanction
        {
            UserId = userId,
            Type = SanctionType.AccountBan,
            Reason = "Retention sweep fixture.",
            IssuedByUserId = userId,
            IssuedAt = Now.AddDays(-90),
            ExpiresAt = expiresAt,
            LiftedAt = liftedAt,
            LiftedByUserId = liftedAt is null ? null : userId,
            LiftReason = liftedAt is null ? null : "Retention sweep fixture."
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string?> MarkerOfAsync(KiriChessDbContext db, Guid userId) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.BanEvasionEmailHash).SingleAsync();

    private static async Task<RetentionSweepResult> RunSweepAsync(
        KiriChessDbContext db, int refreshTokenGraceDays = 30, int externalCodeGraceDays = 1,
        int twoFactorChallengeGraceDays = 1, int apiTokenGraceDays = 30,
        int banEvasionMarkerGraceDays = 30)
    {
        var options = Options.Create(new RetentionOptions
        {
            RefreshTokenGraceDays = refreshTokenGraceDays,
            ExternalCodeGraceDays = externalCodeGraceDays,
            TwoFactorChallengeGraceDays = twoFactorChallengeGraceDays,
            ApiTokenGraceDays = apiTokenGraceDays,
            BanEvasionMarkerGraceDays = banEvasionMarkerGraceDays
        });
        var metrics = new KiriChessMetrics(
            new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());
        var sweep = new RetentionSweep(db, options, metrics, NullLogger<RetentionSweep>.Instance);
        return await sweep.RunAsync(Now, CancellationToken.None);
    }

    private static async Task<Guid> CreateUserAsync(KiriChessDbContext db, string tag)
    {
        var user = new ApplicationUser
        {
            UserName = $"{tag}@example.com",
            NormalizedUserName = $"{tag}@example.com".ToUpperInvariant(),
            Email = $"{tag}@example.com",
            NormalizedEmail = $"{tag}@example.com".ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = tag
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> AddRefreshTokenAsync(
        KiriChessDbContext db, Guid userId, DateTimeOffset expiresAt, DateTimeOffset? revokedAt = null)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private static async Task<Guid> AddExternalLoginCodeAsync(
        KiriChessDbContext db, Guid userId, DateTimeOffset expiresAt, DateTimeOffset? consumedAt = null)
    {
        var code = new ExternalLoginCode
        {
            UserId = userId,
            CodeHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            ConsumedAt = consumedAt
        };
        db.ExternalLoginCodes.Add(code);
        await db.SaveChangesAsync();
        return code.Id;
    }

    private static async Task<Guid> AddTwoFactorChallengeAsync(
        KiriChessDbContext db, Guid userId, DateTimeOffset expiresAt, DateTimeOffset? consumedAt = null)
    {
        var challenge = new TwoFactorChallenge
        {
            UserId = userId,
            TicketHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            ConsumedAt = consumedAt
        };
        db.TwoFactorChallenges.Add(challenge);
        await db.SaveChangesAsync();
        return challenge.Id;
    }

    private static async Task<Guid> AddApiTokenAsync(
        KiriChessDbContext db, Guid userId, DateTimeOffset? revokedAt = null, DateTimeOffset? expiresAt = null)
    {
        var token = new ApiToken
        {
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            TokenPrefix = "abcd1234",
            Name = "test",
            CanRead = true,
            CanPlay = false,
            CreatedAt = Now.AddDays(-40),
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }
}
