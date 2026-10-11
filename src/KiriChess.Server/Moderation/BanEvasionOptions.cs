namespace KiriChess.Server.Moderation;

/// <summary>
/// Binds to the <c>BanEvasion</c> configuration section (S-3-1). One key, and it is a secret:
/// <c>BanEvasion:MarkerSecret</c> (env: <c>BanEvasion__MarkerSecret</c>) is the HMAC key that
/// <see cref="BanEvasionMarker"/> uses to derive the pseudonymous marker written onto a banned
/// account's tombstone at erasure.
///
/// <para><b>Unset means off</b>, the same "empty config = inert" shape as <c>TurnstileOptions</c>,
/// <c>EmailOptions</c> and <c>VapidOptions</c>. A bare checkout, local development and every
/// <c>WebApplicationFactory</c> test therefore behave exactly as they did before this feature
/// existed: no marker is written and registration is never refused on one. What must never happen
/// is the third option - deriving a marker from an empty key - because then every banned tombstone
/// would carry the same value and the first re-registration of any address would collide with all
/// of them. <see cref="BanEvasionMarker"/> returns null instead, and logs a warning at the one
/// moment it matters (an active ban being erased with no key configured).</para>
///
/// Bound with <c>Configure&lt;T&gt;</c> rather than an eager <c>From(configuration)</c> read, same
/// rule as <c>RetentionOptions</c> and <c>TwoFactorOptions</c>: the value is resolved through
/// <c>IOptions&lt;T&gt;</c> per scope, so a test's configuration override is picked up.
/// </summary>
public sealed class BanEvasionOptions
{
    public const string SectionName = "BanEvasion";

    /// <summary>
    /// The HMAC key. Never a literal in a tracked file - env-var indirection only, see
    /// <c>.claude/rules/security.md</c> and docs/ops/DEPLOYMENT.md § 9.2. Empty disables the marker.
    ///
    /// <para>Rotating it invalidates every marker already written: existing tombstones keep a value
    /// derived under the old key and no live address will ever hash to it again. That is a
    /// deliberate property rather than a bug - it means the key is the only thing that makes the
    /// stored value linkable to an address at all - but it does mean a rotation quietly ends
    /// enforcement for bans issued before it.</para>
    /// </summary>
    public string MarkerSecret { get; set; } = string.Empty;

    /// <summary>True when a key is configured. Everything in this feature is gated on it.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(MarkerSecret);
}
