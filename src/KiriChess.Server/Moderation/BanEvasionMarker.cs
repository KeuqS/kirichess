using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Moderation;

/// <summary>
/// S-3-1. Makes TERMS § 6's promise true: "sanctions survive account deletion ... precisely so that
/// deleting your account and registering again cannot be used to launder a ban."
///
/// <para>The retention half of that promise was already true (the <c>UserSanctions</c> foreign key is
/// <c>Restrict</c>, so the row survives erasure). The prevention half was not implemented at all: the
/// retained row points at a tombstone <see cref="Guid"/> and is only ever read by
/// <see cref="SanctionPolicy.BlockingSanctionAsync"/> keyed on the <em>new</em> account's id, so it
/// could never match. Erasure nulls <c>Email</c>/<c>NormalizedEmail</c> and resets
/// <c>DisplayName</c>, releasing both back into the pool, and re-registration with the same address
/// produced a clean account.</para>
///
/// <para><b>What this stores, and why it is not a re-identification hazard.</b> When an account is
/// erased while an <see cref="SanctionType.AccountBan"/> is active, a keyed HMAC-SHA256 of the
/// account's <c>NormalizedEmail</c> is written to
/// <see cref="ApplicationUser.BanEvasionEmailHash"/> immediately before the column is nulled.
/// Registration hashes the candidate address the same way and refuses on a match. The stored value
/// is one-way and keyed: without <c>BanEvasion:MarkerSecret</c> it cannot be brute-forced back to an
/// address the way a bare SHA-256 of an email can (the space of real addresses is small enough to
/// enumerate), and the key never leaves the server's configuration. Erasure therefore stays
/// genuinely irreversible for the player's own data - nothing here can reconstitute the address, the
/// display name, or anything else that was scrubbed.</para>
///
/// <para><b>Lifetime is query-time first, sweep second.</b> A match only refuses registration while
/// the tombstone still carries an <em>unlifted, unexpired</em> <c>AccountBan</c>, so lifting a ban or
/// letting a temporary one expire stops the refusal on the next request, with no job in the loop. The
/// retention sweep then clears the column itself once no such ban has been active for
/// <c>Retention:BanEvasionMarkerGraceDays</c>, so the identifier does not outlive its purpose. See
/// docs/decisions/RETENTION.md § 1 and § 2.</para>
///
/// <para><b>Scope is exactly one sanction type.</b> A <see cref="SanctionType.Warning"/> or a
/// <see cref="SanctionType.RatedPlayBan"/> writes nothing, and an unsanctioned account writes
/// nothing: erasure for those accounts is byte-for-byte what it was before this existed. Only a full
/// account ban is a thing that re-registration would launder, because only a full account ban is
/// something a new account escapes.</para>
///
/// Scoped, like <see cref="SanctionService"/> and unlike <see cref="SanctionPolicy"/>: it takes a
/// <see cref="KiriChessDbContext"/> and is only ever called from a request handler.
/// </summary>
public sealed class BanEvasionMarker
{
    /// <summary>
    /// Domain separation, so the same secret used for anything else later cannot produce a value
    /// that collides with a marker. The <c>v1</c> is deliberate: changing the derivation means
    /// changing this string, which invalidates every stored marker in one visible edit rather than
    /// silently producing two incompatible generations of value in the same column.
    /// </summary>
    private const string DomainSeparator = "kirichess:ban-evasion:v1:";

    private readonly KiriChessDbContext _db;
    private readonly BanEvasionOptions _options;
    private readonly ILogger<BanEvasionMarker> _logger;

    public BanEvasionMarker(
        KiriChessDbContext db, IOptions<BanEvasionOptions> options, ILogger<BanEvasionMarker> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Whether a marker secret is configured at all. Everything below is inert when it is not.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// The marker for an Identity-normalised email address, or null when no secret is configured or
    /// the address is blank. Never derives from an empty key: a marker keyed on nothing would be the
    /// same value for every banned tombstone, and the first re-registration of any address at all
    /// would then be refused.
    /// </summary>
    public string? MarkerFor(string? normalizedEmail)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(normalizedEmail)) return null;

        var key = Encoding.UTF8.GetBytes(_options.MarkerSecret);
        var message = Encoding.UTF8.GetBytes(DomainSeparator + normalizedEmail);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, message));
    }

    /// <summary>
    /// The marker to store for an account being erased, plus the warning that has to be emitted when
    /// there is a ban to mark and no key to mark it with. Returns null when nothing should be stored.
    ///
    /// <paramref name="hasActiveAccountBan"/> is decided by the caller, inside the erasure
    /// transaction's own read, rather than through <see cref="SanctionPolicy"/>: that policy
    /// deliberately fails open on a database error, which is right for a login check and wrong here,
    /// where failing open means silently not writing the marker.
    /// </summary>
    public string? MarkerForErasure(string? normalizedEmail, bool hasActiveAccountBan, Guid userId)
    {
        if (!hasActiveAccountBan) return null;

        if (!_options.Enabled)
        {
            // Loud, and at the one instant it is actionable. Nothing throws: an unset secret must
            // never turn an Art. 17 erasure request into a 500 - the right to erasure does not wait
            // on an operator having set an optional key.
            _logger.LogWarning(
                "Account {UserId} was erased while an account ban was active, but BanEvasion:MarkerSecret "
                + "is not configured, so no ban-evasion marker was written and the address is free to "
                + "re-register. Set BanEvasion__MarkerSecret (docs/ops/DEPLOYMENT.md section 9.2).",
                userId);
            return null;
        }

        return MarkerFor(normalizedEmail);
    }

    /// <summary>
    /// Whether registration under <paramref name="normalizedEmail"/> must be refused: some tombstone
    /// carries this address's marker <em>and</em> still has an unlifted, unexpired account ban.
    ///
    /// <para>Deliberately does not swallow database errors. <see cref="SanctionPolicy"/> fails open
    /// because a database hiccup there would lock every existing player out of the site; here the
    /// caller is the register handler, which is about to write to the same database anyway, so a
    /// failure is a failed registration either way and a silent "allowed" would be the one outcome
    /// this check exists to prevent.</para>
    /// </summary>
    public async Task<bool> IsRegistrationRefusedAsync(string? normalizedEmail, CancellationToken ct = default)
    {
        var marker = MarkerFor(normalizedEmail);
        if (marker is null) return false;

        // Captured once - DateTimeOffset.UtcNow inside an expression tree is both a translation and a
        // nondeterminism hazard, same note as SanctionPolicy.BlockingSanctionAsync.
        var now = DateTimeOffset.UtcNow;

        return await _db.Users.AnyAsync(u =>
            u.DeletedAt != null
            && u.BanEvasionEmailHash == marker
            && _db.UserSanctions.Any(s =>
                s.UserId == u.Id
                && s.Type == SanctionType.AccountBan
                && s.LiftedAt == null
                && (s.ExpiresAt == null || s.ExpiresAt > now)), ct);
    }
}
