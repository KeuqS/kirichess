using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Account;

/// <summary>The opaque, single-use handshake value a signed-in browser attaches to a donate link as <c>client_reference_id</c>.</summary>
public sealed record DonationReferenceDto(string Reference, DateTimeOffset ExpiresAt);

/// <summary>The caller's own patron badge. <c>Tier</c> is always <see cref="PatronBadge.For"/>, never the raw column, so a lapsed one-off reads as None here exactly as it does everywhere else.</summary>
public sealed record PatronStatusDto(
    bool IsPatron,
    [property: StringEnum(typeof(PatronTier))] string Tier,
    [property: StringEnum(typeof(PatronKind))] string Kind,
    DateTimeOffset? Since,
    DateTimeOffset? ExpiresAt);
