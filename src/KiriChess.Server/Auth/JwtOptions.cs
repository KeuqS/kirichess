namespace KiriChess.Server.Auth;

/// <summary>Binds to the <c>Jwt</c> configuration section for token issuance/validation.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "kirichess";
    public string Audience { get; set; } = "kirichess";
    public int ExpiryMinutes { get; set; } = 15;
    public int RefreshTokenExpiryDays { get; set; } = 30;
}
