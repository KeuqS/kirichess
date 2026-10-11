using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KiriChess.Server.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace KiriChess.Server.Auth;

/// <summary>An issued access token plus the instant it expires, for echoing back to the client.</summary>
public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Issues signed JWTs for authenticated <see cref="ApplicationUser"/>s.</summary>
public sealed class TokenService
{
    private readonly JwtOptions _opt;
    public TokenService(IOptions<JwtOptions> opt) => _opt = opt.Value;

    /// <summary>
    /// Issues the access token. <paramref name="roles"/> is optional and defaults to none: the only
    /// role in this codebase is Admin (Phase 8.5), the overwhelming majority of tokens carry no role,
    /// and a null default keeps every pre-8.5 call site and test compiling unchanged.
    ///
    /// The claim is emitted as <see cref="ClaimTypes.Role"/>. JwtSecurityTokenHandler's default
    /// outbound map shortens it to "role" on the wire and the default inbound map expands it back, and
    /// Program.cs sets TokenValidationParameters.RoleClaimType to ClaimTypes.Role so the two ends
    /// agree explicitly rather than by coincidence. This round-trip is pinned by a test - do not
    /// "simplify" either end without running it. The same hazard is why the Phase 9d.2 second-factor
    /// claim is named "mfa" rather than the JWT-standard "amr" - see <see cref="TwoFactorClaims"/>.
    /// </summary>
    public IssuedAccessToken CreateToken(ApplicationUser user, IEnumerable<string>? roles = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_opt.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new("displayName", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (roles is not null)
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        // Phase 9d.2: what ModerationPolicies.Admin requires on top of the role. Asserts "this account
        // has 2FA enabled", which coincides with "this session passed a check" because enabling or
        // disabling 2FA revokes every refresh token for the account - no token predating the change
        // survives to carry a stale claim. See TwoFactorClaims for why the name is "mfa" and not "amr".
        if (user.TwoFactorEnabled)
            claims.Add(new Claim(TwoFactorClaims.ClaimType, TwoFactorClaims.Enabled));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
