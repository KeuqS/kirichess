using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The half of <see cref="RatingInactivity"/> that needs no database: how many rating periods a row
/// has missed, and what its RD becomes. Glicko-2 has two updates, the per-game one and the RD
/// inflation an idle player accrues between periods, and only the first was ever reachable in
/// production, so an inactive rating stayed certain forever.
/// </summary>
public sealed class RatingInactivityMathTests
{
    private static readonly TimeSpan Period = RatingConstants.RatingPeriod;
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_row_touched_inside_the_current_period_has_missed_none()
    {
        Assert.Equal(0, RatingInactivity.PeriodsElapsed(Now, Now, Period));
        Assert.Equal(0, RatingInactivity.PeriodsElapsed(Now - Period + TimeSpan.FromSeconds(1), Now, Period));
    }

    [Fact]
    public void Whole_periods_only_are_counted()
    {
        Assert.Equal(1, RatingInactivity.PeriodsElapsed(Now - Period, Now, Period));
        Assert.Equal(1, RatingInactivity.PeriodsElapsed(Now - Period - TimeSpan.FromDays(6), Now, Period));
        Assert.Equal(3, RatingInactivity.PeriodsElapsed(Now - (Period * 3), Now, Period));
    }

    [Fact]
    public void Inflation_raises_rd_and_touches_nothing_else()
    {
        var before = new Glicko2Rating(1732.5, 48.0, 0.061);

        var after = RatingInactivity.Inflate(before, periods: 1);

        Assert.True(after.Rd > before.Rd, $"Rd was {after.Rd}");
        Assert.Equal(before.Rating, after.Rating);
        Assert.Equal(before.Volatility, after.Volatility);
    }

    [Fact]
    public void Zero_periods_is_the_identity()
    {
        var before = new Glicko2Rating(1732.5, 48.0, 0.061);
        Assert.Equal(before, RatingInactivity.Inflate(before, periods: 0));
    }

    [Fact]
    public void Inflation_is_capped_at_the_default_rd()
    {
        var before = new Glicko2Rating(1500, 48.0, 0.06);

        // Far more idle periods than it takes to reach the cap. DefaultRd is "we know nothing about
        // this player"; nothing is less certain than that, and exceeding it would hand a returning
        // player more rating swing than a brand new account gets.
        var after = RatingInactivity.Inflate(before, periods: 100_000);

        Assert.Equal(RatingConstants.DefaultRd, after.Rd);
        Assert.Equal(1500, after.Rating);
    }

    // The rate is slow by design, and this pins the fact rather than leaving it as folklore: one
    // idle week at the default volatility adds RD in quadrature, so the increase is single digits.
    [Fact]
    public void One_idle_period_adds_the_volatility_in_quadrature()
    {
        var after = RatingInactivity.Inflate(new Glicko2Rating(1500, 48.0, 0.06), periods: 1);

        var expected = Math.Sqrt(48.0 * 48.0 + Math.Pow(0.06 * 173.7178, 2));
        Assert.True(Math.Abs(after.Rd - expected) < 0.01, $"Rd was {after.Rd}, expected about {expected}");
    }
}

/// <summary>
/// <see cref="RatingInactivitySweep"/> against a real Postgres. Requires Docker; skips cleanly when
/// no Docker daemon is reachable. Fixture scaffolding follows <see cref="RetentionSweepTests"/>.
///
/// Redis is deliberately left disabled throughout: this sweep is a correction to the rating system
/// itself, driven entirely from Postgres, and must work on a deploy that has no Redis at all. The
/// leaderboard write-through it performs no-ops in that configuration.
/// </summary>
public sealed class RatingInactivitySweepTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = RatingConstants.RatingPeriod;

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
                    ["Jwt:Key"] = "rating-inactivity-test-signing-key-at-least-32-bytes",
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
    public async Task A_rating_untouched_for_a_period_gains_rd()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var id = await AddRatingAsync(db, rd: 48, updatedAt: Now - Period - TimeSpan.FromHours(1));

        var result = await Sweep(db).RunAsync(Now);

        Assert.Equal(1, result.RowsInflated);
        var row = await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.True(row.RatingDeviation > 48, $"Rd was {row.RatingDeviation}");
    }

    // Constraint 4 of the design: RatingDeviation and UpdatedAt, and nothing else. A sweep that
    // moved Value would rewrite a player's rating with no game behind it, silently and permanently.
    [SkippableFact]
    public async Task The_sweep_moves_only_the_rating_deviation()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var playedAt = Now - (Period * 2);
        var id = await AddRatingAsync(db, rd: 48, updatedAt: playedAt, value: 1732.5, volatility: 0.061, gamesPlayed: 41);
        var lastPlayed = (await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).LastPlayedAt;

        await Sweep(db).RunAsync(Now);

        var row = await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(1732.5, row.Value);
        Assert.Equal(0.061, row.Volatility);
        Assert.Equal(41, row.GamesPlayed);
        // Last played is the established board's recency clock. Advancing it here would put the
        // idle player back on that board for as long as their deviation kept rising.
        Assert.Equal(lastPlayed, row.LastPlayedAt);
    }

    [SkippableFact]
    public async Task A_rating_touched_inside_the_current_period_is_left_alone()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var id = await AddRatingAsync(db, rd: 48, updatedAt: Now - Period + TimeSpan.FromHours(1));

        var result = await Sweep(db).RunAsync(Now);

        Assert.Equal(0, result.RowsInflated);
        var row = await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(48, row.RatingDeviation);
    }

    // Constraint 3: idempotence. This is the property that decides whether the job is safe to own,
    // because there is no error and no symptom when it is wrong - just a rating that has quietly
    // been made twice as uncertain as it should be, on a restart or on a second instance.
    [SkippableFact]
    public async Task Running_twice_in_one_period_does_not_double_inflate()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var id = await AddRatingAsync(db, rd: 48, updatedAt: Now - Period - TimeSpan.FromHours(1));

        var first = await Sweep(db).RunAsync(Now);
        var afterFirst = (await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).RatingDeviation;

        // Same instant, and again a few hours later: neither is a new rating period.
        var second = await Sweep(db).RunAsync(Now);
        var third = await Sweep(db).RunAsync(Now + TimeSpan.FromHours(6));

        Assert.Equal(1, first.RowsInflated);
        Assert.Equal(0, second.RowsInflated);
        Assert.Equal(0, third.RowsInflated);
        Assert.Equal(afterFirst, (await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).RatingDeviation);
    }

    // The next period does inflate again: idempotence must not become "inflates once, ever".
    [SkippableFact]
    public async Task The_next_period_inflates_again()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var id = await AddRatingAsync(db, rd: 48, updatedAt: Now - Period);

        await Sweep(db).RunAsync(Now);
        var afterFirst = (await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).RatingDeviation;

        var second = await Sweep(db).RunAsync(Now + Period);

        Assert.Equal(1, second.RowsInflated);
        Assert.True((await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).RatingDeviation > afterFirst);
    }

    [SkippableFact]
    public async Task Rd_is_capped_at_the_default_and_a_capped_row_is_then_never_written_again()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var abandonedAt = Now - (Period * 5_000); // long past the point where RD saturates
        var id = await AddRatingAsync(db, rd: 48, updatedAt: abandonedAt);

        await Sweep(db).RunAsync(Now);

        var row = await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(RatingConstants.DefaultRd, row.RatingDeviation);

        // Already at the cap, so there is nothing left to do and the row must not be churned.
        var again = await Sweep(db).RunAsync(Now + (Period * 10));
        Assert.Equal(0, again.RowsInflated);
        Assert.Equal(RatingConstants.DefaultRd,
            (await db.Ratings.AsNoTracking().SingleAsync(r => r.Id == id)).RatingDeviation);
    }

    // Constraint 5: Redis is optional, and this whole fixture runs with it disabled. Asserting the
    // sweep completes here is the assertion that a Redis-less deploy still gets its rating
    // correction, rather than an exception out of the write-through.
    [SkippableFact]
    public async Task The_sweep_works_with_redis_disabled()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.False(scope.ServiceProvider.GetRequiredService<IRedisConnection>().Enabled);
        await AddRatingAsync(db, rd: 48, updatedAt: Now - (Period * 2));

        var result = await Sweep(db).RunAsync(Now);

        Assert.Equal(1, result.RowsInflated);
    }

    private static RatingInactivitySweep Sweep(KiriChessDbContext db)
    {
        var connection = new RedisConnection(new RedisOptions("", "kirichess:", 30, 10, 5), new InstanceIdentity("test"));
        var cache = new LeaderboardCache(connection, NullLogger<LeaderboardCache>.Instance);
        return new RatingInactivitySweep(db, cache, NullLogger<RatingInactivitySweep>.Instance);
    }

    /// <summary>
    /// Seeds exactly one rating row and, first, removes every row a previous test in this class left
    /// behind. The sweep is table-wide by design, so a leftover row would land in the next test's
    /// RowsInflated count and make these assertions depend on execution order.
    /// </summary>
    private static async Task<Guid> AddRatingAsync(
        KiriChessDbContext db, double rd, DateTimeOffset updatedAt,
        double value = 1500, double volatility = 0.06, int gamesPlayed = 20)
    {
        await db.Ratings.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var user = new ApplicationUser
        {
            UserName = $"inactive-{Guid.NewGuid():N}@example.com",
            Email = $"inactive-{Guid.NewGuid():N}@example.com",
            DisplayName = $"Inactive {Guid.NewGuid():N}"[..20],
        };
        db.Users.Add(user);

        var rating = new Rating
        {
            UserId = user.Id,
            Pool = "steal1",
            Value = value,
            RatingDeviation = rd,
            Volatility = volatility,
            GamesPlayed = gamesPlayed,
            LastPlayedAt = updatedAt,
            UpdatedAt = updatedAt,
        };
        db.Ratings.Add(rating);
        await db.SaveChangesAsync();
        return rating.Id;
    }
}
