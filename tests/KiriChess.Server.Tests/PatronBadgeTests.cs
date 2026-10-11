using KiriChess.Server.Account;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>Pure, no database required: the one-off badge life the donate page states.</summary>
public sealed class PatronOptionsTests
{
    [Fact]
    public void Default_one_off_grant_is_one_month()
    {
        var options = PatronOptions.From(new ConfigurationBuilder().Build());
        Assert.Equal(1, options.OneTimeGrantMonths);
    }
}

/// <summary>Pure, no database required: the amount-to-tier threshold table.</summary>
public sealed class PatronTiersTests
{
    [Theory]
    [InlineData(500, PatronTier.Pawn)]
    [InlineData(999, PatronTier.Pawn)]
    [InlineData(1000, PatronTier.Knight)]
    [InlineData(1500, PatronTier.Bishop)]
    [InlineData(2500, PatronTier.Rook)]
    [InlineData(5000, PatronTier.Queen)]
    [InlineData(7500, PatronTier.Queen)]
    [InlineData(300, PatronTier.Pawn)]
    [InlineData(0, PatronTier.None)]
    [InlineData(-100, PatronTier.None)]
    public void FromAmountCents_maps_thresholds(long cents, PatronTier expected)
    {
        Assert.Equal(expected, PatronTiers.FromAmountCents(cents));
    }
}

/// <summary>Pure, no database required: PatronBadge.For and TierExpression must agree.</summary>
public sealed class PatronBadgeTests
{
    [Fact]
    public void For_and_TierExpression_agree_over_active_lapsed_never_and_both_monthly_shapes()
    {
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var active = new ApplicationUser
        {
            IsPatron = true, PatronTier = PatronTier.Rook,
            PatronKind = PatronKind.OneTime, PatronExpiresAt = now.AddDays(1)
        };
        var lapsed = new ApplicationUser
        {
            IsPatron = true, PatronTier = PatronTier.Bishop,
            PatronKind = PatronKind.OneTime, PatronExpiresAt = now.AddDays(-1)
        };
        var never = new ApplicationUser { IsPatron = false, PatronTier = PatronTier.None };
        var monthly = new ApplicationUser
        {
            IsPatron = true, PatronTier = PatronTier.Queen,
            PatronKind = PatronKind.Monthly, PatronExpiresAt = null
        };

        // The case the Monthly clause exists for: a one-off bought during a subscription banks its
        // expiry in PatronExpiresAt (PatronService.GrantAsync), so a long-running subscriber can be
        // sitting on a date that has already passed. The subscription, not the column, is what keeps
        // the badge alive, and without the clause this account's badge would vanish while they pay.
        var monthlyWithLapsedBankedOneOff = new ApplicationUser
        {
            IsPatron = true, PatronTier = PatronTier.Knight,
            PatronKind = PatronKind.Monthly, PatronExpiresAt = now.AddDays(-1)
        };

        var users = new[] { active, lapsed, never, monthly, monthlyWithLapsedBankedOneOff };
        var compiled = PatronBadge.TierExpression(now).Compile();
        foreach (var user in users)
            Assert.Equal(PatronBadge.For(user, now), compiled(user));

        Assert.Equal(PatronTier.Rook, PatronBadge.For(active, now));
        Assert.Equal(PatronTier.None, PatronBadge.For(lapsed, now));
        Assert.Equal(PatronTier.None, PatronBadge.For(never, now));
        Assert.Equal(PatronTier.Queen, PatronBadge.For(monthly, now));
        Assert.Equal(PatronTier.Knight, PatronBadge.For(monthlyWithLapsedBankedOneOff, now));
    }
}

/// <summary>
/// PatronService against a real Postgres. Requires Docker; skips cleanly when no Docker daemon is
/// reachable. Fixture scaffolding is copied verbatim from RetentionSweepTests. PatronService is built
/// by hand (not resolved from the factory) so each test can drive time with its own FakeGameClock.
/// </summary>
public sealed class PatronServiceTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "patron-badge-test-signing-key-at-least-32-bytes",
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
    public async Task One_off_grant_sets_tier_kind_and_an_expiry_months_out()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "one-off-grant");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow);

        var outcome = await service.GrantAsync(reference, 2500, isSubscription: false, subscriptionId: null, customerId: "cus_1", CancellationToken.None);

        Assert.Equal(PatronGrantOutcome.Granted, outcome);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.True(user.IsPatron);
        Assert.Equal(PatronTier.Rook, user.PatronTier);
        Assert.Equal(PatronKind.OneTime, user.PatronKind);
        Assert.Equal(clock.UtcNow, user.PatronSince);
        Assert.Equal(clock.UtcNow.AddMonths(12), user.PatronExpiresAt);
        Assert.Equal("cus_1", user.StripeCustomerId);
    }

    [SkippableFact]
    public async Task Subscription_grant_sets_monthly_kind_and_stores_subscription_id()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "sub-grant");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow);

        var outcome = await service.GrantAsync(reference, 5000, isSubscription: true, subscriptionId: "sub_1", customerId: "cus_2", CancellationToken.None);

        Assert.Equal(PatronGrantOutcome.Granted, outcome);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(PatronTier.Queen, user.PatronTier);
        Assert.Equal(PatronKind.Monthly, user.PatronKind);
        // Null because this account had never banked one-off life, not because a subscription grant
        // clears the column - it deliberately leaves it alone, see the one-off-on-top test below.
        Assert.Null(user.PatronExpiresAt);
        Assert.Equal("sub_1", user.StripeSubscriptionId);
    }

    [SkippableFact]
    public async Task Two_one_off_grants_stack_the_expiry()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "stack-expiry");
        var firstRef = await AddReferenceAsync(db, userId, clock.UtcNow);

        await service.GrantAsync(firstRef, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);
        var firstExpiry = (await db.Users.SingleAsync(u => u.Id == userId)).PatronExpiresAt;

        clock.Advance(TimeSpan.FromDays(1));
        var secondRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(secondRef, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);

        var secondExpiry = (await db.Users.SingleAsync(u => u.Id == userId)).PatronExpiresAt;

        Assert.Equal(firstExpiry!.Value.AddMonths(12), secondExpiry);
    }

    [SkippableFact]
    public async Task One_off_on_top_of_an_active_monthly_badge_banks_expiry_without_downgrading()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "monthly-plus-topup");
        var subRef = await AddReferenceAsync(db, userId, clock.UtcNow);

        // Queen monthly subscription first.
        await service.GrantAsync(subRef, 5000, isSubscription: true, subscriptionId: "sub_top", customerId: null, CancellationToken.None);

        // A 5 EUR one-off top-up must not demote Queen to Pawn, and must not turn the account into a
        // OneTime badge while the subscription is live. It must still bank its twelve months: the
        // donor paid for them, and RevokeBySubscriptionAsync is what hands them back.
        var topUpRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(topUpRef, 500, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(PatronTier.Queen, user.PatronTier);
        Assert.Equal(PatronKind.Monthly, user.PatronKind);
        Assert.Equal(clock.UtcNow.AddMonths(12), user.PatronExpiresAt);

        // The banked date is invisible while the subscription lives: a Monthly badge does not lapse,
        // even once that date has passed.
        Assert.Equal(PatronTier.Queen, PatronBadge.For(user, clock.UtcNow.AddMonths(18)));
    }

    [SkippableFact]
    public async Task Cancelling_a_subscription_falls_back_to_the_one_off_life_already_paid_for()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "cancel-keeps-topup");
        var subRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(subRef, 5000, isSubscription: true, subscriptionId: "sub_fallback", customerId: null, CancellationToken.None);

        var topUpRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(topUpRef, 2500, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);
        var banked = (await db.Users.SingleAsync(u => u.Id == userId)).PatronExpiresAt;

        // Two months later they cancel. The one-off still has ten months to run and must survive.
        clock.Advance(TimeSpan.FromDays(60));
        Assert.True(await service.RevokeBySubscriptionAsync("sub_fallback", CancellationToken.None));

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.True(user.IsPatron);
        Assert.Equal(PatronTier.Queen, user.PatronTier); // never downgraded by the cancellation
        Assert.Equal(PatronKind.OneTime, user.PatronKind);
        Assert.Equal(banked, user.PatronExpiresAt);
        Assert.Null(user.StripeSubscriptionId);
        Assert.NotNull(user.PatronSince);
        Assert.Equal(PatronTier.Queen, PatronBadge.For(user, clock.UtcNow));

        // And it still lapses on its own once that banked date passes, with nothing sweeping the row.
        Assert.Equal(PatronTier.None, PatronBadge.For(user, banked!.Value.AddDays(1)));
    }

    [SkippableFact]
    public async Task Cancelling_a_subscription_whose_banked_one_off_has_already_lapsed_clears_outright()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "cancel-after-topup-lapsed");
        var subRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(subRef, 5000, isSubscription: true, subscriptionId: "sub_stale", customerId: null, CancellationToken.None);

        var topUpRef = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(topUpRef, 500, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);

        // Subscribed for two more years, so the banked twelve months ran out long ago. There is
        // nothing left to fall back to and the badge goes.
        clock.Advance(TimeSpan.FromDays(730));
        Assert.True(await service.RevokeBySubscriptionAsync("sub_stale", CancellationToken.None));

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.False(user.IsPatron);
        Assert.Equal(PatronTier.None, user.PatronTier);
        Assert.Equal(PatronKind.None, user.PatronKind);
        Assert.Null(user.PatronExpiresAt);
        Assert.Null(user.StripeSubscriptionId);
    }

    [SkippableFact]
    public async Task PatronBadge_For_reads_None_once_the_clock_passes_expiry_with_no_sweep()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "lapses-on-its-own");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(reference, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(PatronTier.Knight, PatronBadge.For(user, clock.UtcNow));

        // 12 months and a day out: past the grant's own expiry, with nothing having swept the row.
        var later = clock.UtcNow.AddMonths(12).AddDays(1);
        Assert.Equal(PatronTier.None, PatronBadge.For(user, later));
        Assert.True(user.IsPatron); // the raw column is untouched; PatronBadge.For is the only lapse rule
    }

    [SkippableFact]
    public async Task RevokeBySubscriptionAsync_clears_the_badge_unknown_id_is_a_no_op()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "revoke-subscription");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow);
        await service.GrantAsync(reference, 5000, isSubscription: true, subscriptionId: "sub_revoke", customerId: null, CancellationToken.None);

        var unknown = await service.RevokeBySubscriptionAsync("sub_does_not_exist", CancellationToken.None);
        Assert.False(unknown);

        var revoked = await service.RevokeBySubscriptionAsync("sub_revoke", CancellationToken.None);
        Assert.True(revoked);

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.False(user.IsPatron);
        Assert.Equal(PatronTier.None, user.PatronTier);
        Assert.Equal(PatronKind.None, user.PatronKind);
        Assert.Null(user.PatronSince);
        Assert.Null(user.StripeSubscriptionId);
    }

    /// <summary>
    /// C-12-3. <see cref="PatronService.RevokeBySubscriptionAsync"/> looks up one account by
    /// subscription id. The index is what stops a second account, including a write that bypasses
    /// the service, from keeping a badge after the first is revoked. Both rows are saved with a
    /// null id first, which the partial index must allow.
    /// </summary>
    [SkippableFact]
    public async Task Two_accounts_cannot_share_a_subscription_id()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var first = await CreateUserAsync(db, "sub-unique-a");
        var second = await CreateUserAsync(db, "sub-unique-b");

        var holder = await db.Users.SingleAsync(u => u.Id == first);
        var other = await db.Users.SingleAsync(u => u.Id == second);
        Assert.Null(holder.StripeSubscriptionId);
        Assert.Null(other.StripeSubscriptionId);

        holder.StripeSubscriptionId = "sub_shared";
        await db.SaveChangesAsync();

        other.StripeSubscriptionId = "sub_shared";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_reference_is_single_use()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "single-use-reference");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow);

        var first = await service.GrantAsync(reference, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);
        Assert.Equal(PatronGrantOutcome.Granted, first);

        var tierAfterFirst = (await db.Users.SingleAsync(u => u.Id == userId)).PatronTier;

        var second = await service.GrantAsync(reference, 5000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);
        Assert.Equal(PatronGrantOutcome.NoReference, second);

        var tierAfterSecond = (await db.Users.SingleAsync(u => u.Id == userId)).PatronTier;
        Assert.Equal(tierAfterFirst, tierAfterSecond); // the reused-value attempt changed nothing
    }

    [SkippableFact]
    public async Task An_expired_reference_grants_nothing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var clock = new FakeGameClock();
        var service = NewService(db, clock);
        var userId = await CreateUserAsync(db, "expired-reference");
        var reference = await AddReferenceAsync(db, userId, clock.UtcNow.AddHours(-25)); // 24h life, already past it

        var outcome = await service.GrantAsync(reference, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None);

        Assert.Equal(PatronGrantOutcome.NoReference, outcome);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.False(user.IsPatron);
    }

    [SkippableFact]
    public async Task Concurrent_grants_of_the_same_reference_consume_once()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        Guid userId;
        string reference;
        var clock = new FakeGameClock();
        using (var seed = _factory!.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            userId = await CreateUserAsync(db, "concurrent-grant");
            reference = await AddReferenceAsync(db, userId, clock.UtcNow);
        }

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var serviceA = NewService(scopeA.ServiceProvider.GetRequiredService<KiriChessDbContext>(), clock);
        var serviceB = NewService(scopeB.ServiceProvider.GetRequiredService<KiriChessDbContext>(), clock);

        var outcomes = await Task.WhenAll(
            serviceA.GrantAsync(reference, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None),
            serviceB.GrantAsync(reference, 1000, isSubscription: false, subscriptionId: null, customerId: null, CancellationToken.None));

        Assert.Equal(1, outcomes.Count(o => o == PatronGrantOutcome.Granted));
        Assert.Equal(1, outcomes.Count(o => o == PatronGrantOutcome.NoReference));

        using var check = _factory.Services.CreateScope();
        var after = await check.ServiceProvider.GetRequiredService<KiriChessDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(clock.UtcNow.AddMonths(12), after.PatronExpiresAt);
    }

    private static PatronService NewService(KiriChessDbContext db, FakeGameClock clock) =>
        new(db, new PatronOptions(OneTimeGrantMonths: 12), clock, NullLogger<PatronService>.Instance);

    private static async Task<Guid> CreateUserAsync(KiriChessDbContext db, string tag)
    {
        var user = new ApplicationUser();
        user.UserName = tag + "@example.com";
        user.NormalizedUserName = (tag + "@example.com").ToUpperInvariant();
        user.Email = tag + "@example.com";
        user.NormalizedEmail = (tag + "@example.com").ToUpperInvariant();
        user.EmailConfirmed = true;
        user.DisplayName = tag;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> AddReferenceAsync(KiriChessDbContext db, Guid userId, DateTimeOffset createdAt)
    {
        var row = new DonationReference();
        row.UserId = userId;
        row.Reference = Guid.NewGuid().ToString("N");
        row.CreatedAt = createdAt;
        row.ExpiresAt = createdAt.AddHours(24);
        db.DonationReferences.Add(row);
        await db.SaveChangesAsync();
        return row.Reference;
    }
}
