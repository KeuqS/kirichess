using KiriChess.Engine.Config;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation.Content;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Data;

/// <summary>EF Core context for KiriChess: Identity tables plus games/ratings.</summary>
public sealed class KiriChessDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    public KiriChessDbContext(DbContextOptions<KiriChessDbContext> options) : base(options) { }

    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameMove> GameMoves => Set<GameMove>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<RatingHistory> RatingHistories => Set<RatingHistory>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<ExternalLoginCode> ExternalLoginCodes => Set<ExternalLoginCode>();
    public DbSet<TwoFactorChallenge> TwoFactorChallenges => Set<TwoFactorChallenge>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<RematchOffer> RematchOffers => Set<RematchOffer>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<UserSanction> UserSanctions => Set<UserSanction>();

    public DbSet<WebPushSubscription> WebPushSubscriptions => Set<WebPushSubscription>();

    /// <summary>The patron-badge Stripe handshake row. See <see cref="DonationReference"/>.</summary>
    public DbSet<DonationReference> DonationReferences => Set<DonationReference>();

    /// <summary>Phase 10c.4: the four operational kill switches, one row per <see cref="KiriChess.Server.Infrastructure.KillSwitch"/> member. See <see cref="KiriChess.Server.Infrastructure.KillSwitchService"/>.</summary>
    public DbSet<KillSwitchState> KillSwitches => Set<KillSwitchState>();

    /// <summary>AD.6 / 11a.3: the site's entire record of anonymous traffic and the first-session funnel. See <see cref="DailyCounter"/>.</summary>
    public DbSet<DailyCounter> DailyCounters => Set<DailyCounter>();

    // Phase 10a.1. Not a KiriChess entity: this is ASP.NET's Data Protection key ring, and the
    // interface above is what lets PersistKeysToDbContext write into this schema. It lives in the
    // application database rather than beside it because the keys that protect Identity's
    // email-confirmation and password-reset tokens must outlive the container, and Postgres is the
    // only store in this deployment that does. Redis takes over when it is configured (see
    // RedisRegistration), and this stays as the floor underneath that.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity tables

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(64);

            // Phase 8a.4. Same 64 as DisplayName so a grandfathered long name can always be keyed.
            e.Property(u => u.NormalizedDisplayName).HasMaxLength(64);

            // The impersonation control (8a.4). Two accounts cannot hold names that fold to the same
            // identity key - see DisplayNameNormalizer.
            //
            // Phase 8a.2 made it PARTIAL. Erasure gives every tombstone the same DisplayName
            // ("Deleted account", keying to "deletedaccount"), so under a total index the SECOND
            // deletion would fail on a duplicate key. The alternative - a per-user suffix - is a trap:
            // NormalizeForIdentity folds 0->o, 1->l, 4->a and 5->s, so over a hex guid fragment it is
            // not injective ("a4" and "aa" both key to "aa") and the collision would surface as a
            // random 500 on somebody's deletion request. Live accounts keep exactly the guarantee they
            // had; deleted ones are simply not in the index, which is what a partial index is for.
            e.HasIndex(u => u.NormalizedDisplayName)
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL");

            e.Property(u => u.TermsVersion).HasMaxLength(32);

            // Erasure scrubs the row in place rather than deleting it, so nothing cascades from here
            // and no relationship below needed its DeleteBehavior changed. The three Restrict comments
            // on Report, UserSanction and RatingHistory that defer to "8a.2" are answered by that
            // choice, not by a schema change - see AccountDeletionService.
            //
            // C-12-6. No index on DeletedAt. Profile and avatar reads pair it with a primary-key
            // equality, which is one row before the predicate matters. The dormant-account scan is
            // DeletedAt == null, which matches almost every row, so Postgres will not use an index
            // for it, and the index was a write on every update of this table.

            // S-3-1. Hex of an HMAC-SHA256, so exactly 64 characters when present.
            e.Property(u => u.BanEvasionEmailHash).HasMaxLength(64);

            // Deliberately NOT unique. The same address can be banned, erased, re-registered under a
            // lifted ban, banned again and erased again, which is two tombstones carrying the same
            // marker; a unique index would turn that legitimate sequence into a 500 on somebody's
            // erasure request - the same trap the NormalizedDisplayName index had to be made partial
            // for. Filtered to the non-null rows because only the register lookup reads it, and on a
            // healthy deployment almost every row is null.
            e.HasIndex(u => u.BanEvasionEmailHash)
                .HasFilter("\"BanEvasionEmailHash\" IS NOT NULL");

            // Patron badge: enum columns as strings, same convention as Game/Challenge/Report below.
            e.Property(u => u.PatronTier).HasConversion<string>().HasMaxLength(16);
            e.Property(u => u.PatronKind).HasConversion<string>().HasMaxLength(16);
            e.Property(u => u.StripeSubscriptionId).HasMaxLength(255);
            e.Property(u => u.StripeCustomerId).HasMaxLength(255);
            // C-12-3. Revoke looks up one account by subscription id. Unique so a second
            // row cannot keep a paid badge after the first is revoked, and filtered
            // because almost every account is null and the lookup never matches those.
            e.HasIndex(u => u.StripeSubscriptionId)
                .IsUnique()
                .HasFilter("\"StripeSubscriptionId\" IS NOT NULL");
        });

        b.Entity<Game>(e =>
        {
            e.Property(g => g.Result).HasConversion<string>().HasMaxLength(16);
            e.Property(g => g.Termination).HasConversion<string>().HasMaxLength(32);
            e.Property(g => g.RatingPool).HasMaxLength(32);

            e.Property(g => g.ConfigSnapshot)
                .HasConversion(new JsonValueConverter<KiriChessConfig>())
                .Metadata.SetValueComparer(new JsonValueComparer<KiriChessConfig>());
            e.Property(g => g.ConfigSnapshot).HasColumnType("jsonb");

            e.Property(g => g.Moves)
                .HasConversion(new JsonValueConverter<List<MoveRecord>>())
                .Metadata.SetValueComparer(new JsonValueComparer<List<MoveRecord>>());
            e.Property(g => g.Moves).HasColumnType("jsonb");

            e.HasOne(g => g.WhitePlayer).WithMany().HasForeignKey(g => g.WhitePlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(g => g.BlackPlayer).WithMany().HasForeignKey(g => g.BlackPlayerId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(g => g.Result); // boot recovery queries WHERE Result = 'Ongoing'

            // The 8.3 balance query is "finished games in a recent window, grouped by pool".
            e.HasIndex(g => new { g.RatingPool, g.EndedAt });

            // I-1-4. GET /api/games/mine is "my finished games, newest end first", from either seat.
            // A partial index on (player, EndedAt) serves that seek. Ongoing rows have a null
            // EndedAt and stay out of it. The single-column indexes are explicit, not the FK
            // convention: a partial composite leads with the same column, and EF would otherwise
            // drop the FK index even though it does not cover a lookup that includes ongoing games.
            // The history query must keep `EndedAt != null` or Postgres will not use the filter.
            e.HasIndex(g => g.WhitePlayerId);
            e.HasIndex(g => g.BlackPlayerId);
            e.HasIndex(g => new { g.WhitePlayerId, g.EndedAt })
                .HasFilter("\"EndedAt\" IS NOT NULL");
            e.HasIndex(g => new { g.BlackPlayerId, g.EndedAt })
                .HasFilter("\"EndedAt\" IS NOT NULL");
        });

        b.Entity<GameMove>(e =>
        {
            e.HasKey(m => new { m.GameId, m.Ply });
            e.Property(m => m.Move)
                .HasConversion(new JsonValueConverter<MoveRecord>())
                .Metadata.SetValueComparer(new JsonValueComparer<MoveRecord>());
            e.Property(m => m.Move).HasColumnType("jsonb");
            e.HasOne(m => m.Game).WithMany().HasForeignKey(m => m.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GameMove>(e =>
        {
            e.HasKey(m => new { m.GameId, m.Ply });
            e.Property(m => m.Move)
                .HasConversion(new JsonValueConverter<MoveRecord>())
                .Metadata.SetValueComparer(new JsonValueComparer<MoveRecord>());
            e.Property(m => m.Move).HasColumnType("jsonb");
            e.HasOne(m => m.Game).WithMany().HasForeignKey(m => m.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase 9d.8. Unlike RefreshToken/ExternalLoginCode/TwoFactorChallenge, Token is plaintext and
        // re-displayable rather than hashed - see Challenge's own doc comment and README scope
        // decision 1. The index on it is what makes GET/accept/DELETE by token a lookup, not a scan.
        b.Entity<Challenge>(e =>
        {
            e.Property(c => c.Token).HasMaxLength(32);
            e.HasIndex(c => c.Token).IsUnique();

            e.Property(c => c.Pool).HasMaxLength(32);
            e.Property(c => c.Colour).HasConversion<string>().HasMaxLength(16);

            e.Property(c => c.CustomConfig)
                .HasConversion(new JsonValueConverter<KiriChessConfig>())
                .Metadata.SetValueComparer(new JsonValueComparer<KiriChessConfig>());
            e.Property(c => c.CustomConfig).HasColumnType("jsonb").IsRequired(false);

            // "my open challenges" and the per-user cap are both this query.
            e.HasIndex(c => new { c.ChallengerId, c.AcceptedAt, c.CancelledAt });

            // C-12-2. The retention sweep deletes WHERE ExpiresAt < cutoff and does not
            // constrain ChallengerId, so the index above cannot serve it.
            e.HasIndex(c => c.ExpiresAt);

            // Erasure scrubs the user row in place (8a.2) and AccountDeletionService deletes these rows
            // outright, so Restrict here matches Game/Report/UserSanction rather than cascading.
            e.HasOne(c => c.Challenger).WithMany().HasForeignKey(c => c.ChallengerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.AcceptedBy).WithMany().HasForeignKey(c => c.AcceptedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Phase 11a.1. No navigation properties/FKs on purpose: this row is an ephemeral live-offer
        // flag, not a durable record anything else needs to join against (unlike Challenge, nothing
        // reads a RematchOffer back except the accept guard itself), so it carries plain Guid columns.
        b.Entity<RematchOffer>(e =>
        {
            e.HasKey(r => r.GameId);
        });

        b.Entity<Rating>(e =>
        {
            e.Property(r => r.Pool).HasMaxLength(32);
            // now(), not a sentinel. Play A rolls the app back and leaves the schema: an image that
            // has never heard of this column omits it on INSERT, and a NOT NULL column without a
            // default would fail that insert. The current image always writes the value itself.
            e.Property(r => r.LastPlayedAt).HasDefaultValueSql("now()");
            // C-7-6. uint + IsRowVersion maps to the xmin system column. The migration names
            // that column, and Npgsql emits no ALTER TABLE for it.
            e.Property(r => r.Version).IsRowVersion();
            e.HasIndex(r => new { r.UserId, r.Pool }).IsUnique();
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RatingHistory>(e =>
        {
            e.Property(r => r.Pool).HasMaxLength(32);
            e.HasIndex(r => new { r.UserId, r.Pool, r.CreatedAt });
            // C-1-4 / C-8-9. One row per player per game. The (UserId, Pool, CreatedAt) index
            // above is not unique and does not name GameId, so a second persist of an
            // already-rated game used to insert again and double-count. Replay and PGN read
            // these rows with ToDictionary on UserId, which throws if that invariant breaks.
            e.HasIndex(r => new { r.GameId, r.UserId }).IsUnique();
            e.HasOne(r => r.Game).WithMany().HasForeignKey(r => r.GameId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.UserId, t.RevokedAt });
            // C-12-2. The sweep deletes WHERE ExpiresAt < cutoff, with no UserId.
            e.HasIndex(t => t.ExpiresAt);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase 9f.5. Same hashed-secret shape as RefreshToken; no rotation chain, so no
        // ReplacedByTokenId. TokenPrefix is display-only (the first eight characters after kirichess_pat_).
        b.Entity<ApiToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.TokenPrefix).HasMaxLength(8);
            e.Property(t => t.Name).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.UserId, t.RevokedAt });
            // C-12-2. The sweep is (ExpiresAt < cutoff) OR (RevokedAt < cutoff), and neither
            // arm constrains UserId. (UserId, RevokedAt) cannot serve that. Two single-column
            // indexes let Postgres BitmapOr the arms.
            e.HasIndex(t => t.ExpiresAt);
            e.HasIndex(t => t.RevokedAt);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ExternalLoginCode>(e =>
        {
            e.HasIndex(c => c.CodeHash).IsUnique();
            // C-12-2. The sweep deletes WHERE ExpiresAt < cutoff.
            e.HasIndex(c => c.ExpiresAt);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Patron badge Stripe handshake (see DonationReference's own doc comment). Cascade: a
        // reference is meaningless without the account it belongs to, same reasoning as
        // WebPushSubscription below.
        b.Entity<DonationReference>(e =>
        {
            e.Property(r => r.Reference).HasMaxLength(64);
            e.HasIndex(r => r.Reference).IsUnique();
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TwoFactorChallenge>(e =>
        {
            e.HasIndex(c => c.TicketHash).IsUnique();
            // C-12-2. The sweep deletes WHERE ExpiresAt < cutoff.
            e.HasIndex(c => c.ExpiresAt);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Report>(e =>
        {
            e.Property(r => r.Reason).HasConversion<string>().HasMaxLength(32);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(r => r.Details).HasMaxLength(1000);
            e.Property(r => r.ModeratorNote).HasMaxLength(2000);

            // "Everything filed against this account, newest first", the query a future moderator runs.
            e.HasIndex(r => new { r.ReportedUserId, r.CreatedAt });
            // Duplicate suppression (ReportEndpoints) and the /mine endpoint both use this.
            e.HasIndex(r => new { r.ReporterId, r.CreatedAt });

            // Restrict, not Cascade: a report must not vanish because an account was deleted. GDPR
            // deletion (8a.2) needs a deliberate answer here, not a silent cascade.
            e.HasOne(r => r.Reporter).WithMany().HasForeignKey(r => r.ReporterId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.ReportedUser).WithMany().HasForeignKey(r => r.ReportedUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Game).WithMany().HasForeignKey(r => r.GameId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<UserSanction>(e =>
        {
            e.Property(s => s.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Reason).HasMaxLength(500);
            e.Property(s => s.ModeratorNote).HasMaxLength(2000);
            e.Property(s => s.LiftReason).HasMaxLength(500);

            // The enforcement query on every login, refresh and queue join: "active sanctions for this
            // user". Leading with UserId and carrying the two nullability filters is what keeps that
            // off a sequential scan once the table is large.
            e.HasIndex(s => new { s.UserId, s.LiftedAt, s.ExpiresAt });
            // The moderation list: everything issued, newest first.
            e.HasIndex(s => s.IssuedAt);

            // Restrict on both, for the reason Report already documents: a moderation record must not
            // vanish because an account was deleted. GDPR deletion (8a.2) owns the real answer here and
            // must not inherit a silent cascade.
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.IssuedBy).WithMany().HasForeignKey(s => s.IssuedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Report).WithMany().HasForeignKey(s => s.ReportId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<KillSwitchState>(e =>
        {
            e.HasKey(k => k.Name);
            e.Property(k => k.Name).HasMaxLength(32);
        });

        // AD.6 / 11a.3. The composite key IS the data: one row per (UTC day, counter key, campaign
        // source), no surrogate id, so the same triple cannot be counted twice into two rows. Date
        // leads because every read filters a date range. Bounded forever at
        // |CounterKeys.All| x |CounterSources.All| x days, which is what makes an anonymous,
        // unauthenticated write endpoint safe to expose at all (Phase 8a.5 scope
        // decision 5).
        b.Entity<DailyCounter>(e =>
        {
            e.HasKey(c => new { c.Date, c.CounterKey, c.Source });
            e.Property(c => c.CounterKey).HasMaxLength(32);
            e.Property(c => c.Source).HasMaxLength(32);
        });

        // 11b.1 follow-up: web push. Cascade, unlike Report/UserSanction/RatingHistory's Restrict -
        // a subscription is meaningless without the account it belongs to and carries no moderation or
        // audit value, the same reasoning RefreshToken/ExternalLoginCode/TwoFactorChallenge already use.
        b.Entity<WebPushSubscription>(e =>
        {
            e.Property(s => s.Endpoint).HasMaxLength(512);
            e.Property(s => s.P256dh).HasMaxLength(256);
            e.Property(s => s.Auth).HasMaxLength(256);
            e.HasIndex(s => s.Endpoint).IsUnique();
            e.HasIndex(s => s.UserId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncNormalizedDisplayNames();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncNormalizedDisplayNames();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Keeps the derived NormalizedDisplayName in step with DisplayName on every write (Phase 8a.4).
    ///
    /// This lives here rather than at each call site for one concrete reason: the unique index on
    /// NormalizedDisplayName means a single forgotten assignment writes an empty key, and the SECOND
    /// such row fails the insert. Register, external login, moderator rename, DevSeeder and every test
    /// that constructs an ApplicationUser are all paths that could forget. Deriving it once here makes
    /// forgetting impossible instead of merely discouraged.
    ///
    /// DisplayNameService.Apply still sets the value explicitly - that keeps the in-memory entity
    /// correct for a handler that returns it before saving. This recomputes the same string.
    /// </summary>
    private void SyncNormalizedDisplayNames()
    {
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var normalized = DisplayNameNormalizer.NormalizeForIdentity(entry.Entity.DisplayName);
            if (!string.Equals(entry.Entity.NormalizedDisplayName, normalized, StringComparison.Ordinal))
                entry.Entity.NormalizedDisplayName = normalized;
        }
    }
}
