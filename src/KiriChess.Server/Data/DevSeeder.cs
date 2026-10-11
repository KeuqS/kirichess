using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Data;

/// <summary>
/// Local-dev demo data so the leaderboard has something to show on a fresh DB.
/// Opt-in via the "SeedDemoData" config key (set in docker-compose.yml) so it never
/// fires for the WebApplicationFactory-based integration tests, which assert an empty
/// Ratings table. Skips entirely if any rating already exists.
/// </summary>
public static class DevSeeder
{
    private static readonly string[] NamePrefixes =
    {
        "Rook", "Knight", "Bishop", "Pawn", "Zugzwang", "Fianchetto", "Gambit",
        "Tempo", "Fork", "Skewer", "Endgame", "Flank", "Zwischenzug", "Prophylaxis",
        "Blitz", "Sac", "Outpost", "Overload", "Discover", "Waiting",
    };

    private static readonly string[] NameSuffixes =
    {
        "Master", "Hunter", "Storm", "Wizard", "Ghost", "Ranger", "Runner",
        "Slayer", "Sage", "Rider", "Falcon", "Wolf", "Phantom", "Ace", "Fox", "Owl",
    };

    public static async Task SeedAsync(KiriChessDbContext db)
    {
        if (await db.Ratings.AnyAsync()) return;

        var rng = new Random(42);
        var usedNames = new HashSet<string>();
        var users = new List<ApplicationUser>();

        while (users.Count < 45)
        {
            var name = $"{NamePrefixes[rng.Next(NamePrefixes.Length)]}" +
                       $"{NameSuffixes[rng.Next(NameSuffixes.Length)]}{rng.Next(1, 999)}";
            if (!usedNames.Add(name)) continue;

            users.Add(new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = name,
                NormalizedUserName = name.ToUpperInvariant(),
                Email = $"{name.ToLowerInvariant()}@demo.kirichess.local",
                NormalizedEmail = $"{name.ToUpperInvariant()}@DEMO.KIRICHESS.LOCAL",
                EmailConfirmed = true,
                DisplayName = name,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        db.Users.AddRange(users);

        var ratings = new List<Rating>();
        foreach (var user in users)
        {
            foreach (var pool in RatingConstants.OfficialPools)
            {
                var gamesPlayed = rng.Next(15, 400);
                var rd = Math.Clamp(350.0 - gamesPlayed * 1.1 + rng.Next(-15, 16), 45, 350);
                var value = 1500 + rng.Next(-500, 600);

                ratings.Add(new Rating
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Pool = pool,
                    Value = value,
                    RatingDeviation = rd,
                    Volatility = RatingConstants.DefaultVolatility,
                    GamesPlayed = gamesPlayed,
                    LastPlayedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        db.Ratings.AddRange(ratings);
        await db.SaveChangesAsync();
    }
}
