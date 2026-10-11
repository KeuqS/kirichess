namespace KiriChess.Server.Auth;

/// <summary>Wire values for API-token scopes (Phase 9f.5). Exactly two, and no third is to be added.</summary>
public static class ApiTokenScopes
{
    public const string Read = "read";
    public const string Play = "play";
}

/// <summary>
/// Claims stamped on an API-token principal. None of these ride a session JWT, and a token principal
/// must never carry <c>role</c> or <see cref="TwoFactorClaims.ClaimType"/>, even when the owning
/// account is an admin with 2FA.
/// </summary>
public static class ApiTokenClaims
{
    public const string Scope = "scope";
    public const string TokenId = "token_id";
    public const string TokenUse = "token_use";
    public const string Api = "api";
}

/// <summary>Authentication scheme names for the API-token surface (Phase 9f.5).</summary>
public static class ApiTokenDefaults
{
    public const string Scheme = "ApiToken";

    /// <summary>
    /// S-4-3. Long enough to register with a secret scanner, and not a prefix any other
    /// base64 string is likely to share. See <see cref="ApiTokenFormat"/>.
    /// </summary>
    public const string Prefix = "kirichess_pat_";

    /// <summary>URL-safe base64 of 32 random bytes, with the padding stripped.</summary>
    public const int SecretLength = 43;

    /// <summary>Base62 CRC-32 of <see cref="SecretLength"/>, padded on the left with <c>0</c>.</summary>
    public const int ChecksumLength = 6;

    /// <summary>Digit order for <see cref="ChecksumLength"/>. Zero is the first character.</summary>
    public const string ChecksumAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public const int MaxActivePerUser = 10;
    public const int PrefixDisplayLength = 8;

    /// <summary>
    /// S-1-4. Life of a token when <c>ExpiresInDays</c> is omitted. Finite on purpose: a key pasted
    /// into a public bot repo must stop working.
    /// </summary>
    public const int DefaultExpiresInDays = 90;

    /// <summary>Upper bound on <c>ExpiresInDays</c>. A caller cannot mint a token that outlives a year.</summary>
    public const int MaxExpiresInDays = 365;
}

/// <summary>
/// Hub-only policy scheme (Phase 9f.5): selects <see cref="ApiTokenDefaults.Scheme"/> when the
/// bearer/query value starts with <see cref="ApiTokenDefaults.Prefix"/>, otherwise JWT. Must not be the app's default scheme,
/// or a <c>read</c> token would satisfy every existing <c>RequireAuthorization()</c> endpoint.
/// </summary>
public static class HubAuthDefaults
{
    public const string Scheme = "HubAuth";
}

/// <summary>Authorization policy names for API tokens and the game hub's mixed-auth methods.</summary>
public static class ApiTokenPolicies
{
    public const string Read = "api-token-read";
    public const string Play = "api-token-play";
    public const string HubPlay = "hub-play-if-api-token";
}
