using KiriChess.Server.Data;
using KiriChess.Server.Games;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

public sealed record AvatarMutation(bool Ok, string? Error, string? AvatarUrl);

public sealed record PublicAvatarFile(byte[] Bytes, DateTimeOffset UpdatedAt);

/// <summary>
/// Own-account upload/clear plus the anonymous JPEG read. Callers never persist the raw
/// <see cref="IFormFile"/>: the bytes are copied into a capped buffer, validated as a square JPEG,
/// and stored on the user row so erasure is one column assignment.
/// </summary>
public sealed class AvatarService(KiriChessDbContext db, IGameClock clock)
{
    public async Task<AvatarMutation> UploadAsync(Guid userId, IFormFile file, CancellationToken ct)
    {
        if (file.Length <= 0 || file.Length > JpegAvatar.MaxBytes)
            return new AvatarMutation(false, "Profile pictures must be 256 KB or smaller.", null);

        await using var stream = file.OpenReadStream();
        var buffer = new byte[file.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }

        if (read != buffer.Length)
            return new AvatarMutation(false, "That file is not a valid JPEG.", null);

        if (!JpegAvatar.TryValidate(buffer, out var error))
            return new AvatarMutation(false, error, null);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new AvatarMutation(false, null, null);

        user.AvatarImage = buffer;
        user.AvatarUpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new AvatarMutation(true, null, AvatarUrls.For(user.Id, user.AvatarUpdatedAt));
    }

    public async Task<AvatarMutation> ClearAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new AvatarMutation(false, null, null);

        user.AvatarImage = null;
        user.AvatarUpdatedAt = null;
        await db.SaveChangesAsync(ct);
        return new AvatarMutation(true, null, null);
    }

    public async Task<PublicAvatarFile?> GetPublicAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAt == null && u.AvatarImage != null && u.AvatarUpdatedAt != null)
            .Select(u => new { u.AvatarImage, u.AvatarUpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (row?.AvatarImage is null || row.AvatarUpdatedAt is null) return null;
        return new PublicAvatarFile(row.AvatarImage, row.AvatarUpdatedAt.Value);
    }
}
