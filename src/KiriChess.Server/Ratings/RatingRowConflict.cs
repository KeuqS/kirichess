using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KiriChess.Server.Ratings;

/// <summary>
/// C-7-6. A lost update on <see cref="Rating"/> (xmin) and a first-game insert that lost the
/// unique (UserId, Pool) race are the same recovery: drop the rating rows this attempt tracked
/// and read them again. A duplicate <see cref="RatingHistory"/> for one game is a different
/// failure and must not be retried as if the rating row had moved.
/// </summary>
internal static class RatingRowConflict
{
    internal const string UserPoolIndex = "IX_Ratings_UserId_Pool";

    public static bool IsConflict(DbUpdateException ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return true;

        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException pg
                && pg.SqlState == PostgresErrorCodes.UniqueViolation
                && pg.ConstraintName == UserPoolIndex)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="apply"/> and saves. On a rating-row conflict, drops the rating rows
    /// that attempt tracked and runs <paramref name="apply"/> once more. <paramref name="apply"/>
    /// must be safe to repeat: the first attempt's rating and history rows are detached first.
    /// </summary>
    public static async Task CommitAsync(
        KiriChessDbContext db,
        Func<Task> apply,
        Action<DbUpdateException>? onConflict = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await apply();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < 2 && IsConflict(ex))
            {
                onConflict?.Invoke(ex);
                DetachPending(db);
            }
        }
    }

    public static void DetachPending(KiriChessDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<RatingHistory>().ToList())
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }

        foreach (var entry in db.ChangeTracker.Entries<Rating>().ToList())
            entry.State = EntityState.Detached;
    }
}
