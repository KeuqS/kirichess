using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data.Entities;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Tests;

public sealed class TokenServiceTests
{
    [Fact]
    public void CreateToken_issues_jwt_with_subject_and_issuer_claims()
    {
        var options = Options.Create(new JwtOptions
        {
            Key = "unit-test-signing-key-at-least-32-bytes-long",
            Issuer = "kirichess",
            Audience = "kirichess",
            ExpiryMinutes = 60
        });
        var tokenService = new TokenService(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "player@example.com",
            Email = "player@example.com",
            DisplayName = "Player One"
        };

        var issued = tokenService.CreateToken(user);

        Assert.False(string.IsNullOrWhiteSpace(issued.Value));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Value);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("kirichess", jwt.Issuer);
        Assert.True(issued.ExpiresAt > DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Phase 8.5: proves the role round-trip end to end, not just that CreateToken accepts a roles
    /// parameter. TokenValidationParameters.RoleClaimType (Program.cs) is set to ClaimTypes.Role so the
    /// inbound side agrees with the outbound "role" short name JwtSecurityTokenHandler writes - reading
    /// the token back through the same handler and its default inbound claim map is what actually
    /// exercises that agreement.
    /// </summary>
    [Fact]
    public void CreateToken_with_roles_round_trips_as_a_role_claim()
    {
        var options = Options.Create(new JwtOptions
        {
            Key = "unit-test-signing-key-at-least-32-bytes-long",
            Issuer = "kirichess",
            Audience = "kirichess",
            ExpiryMinutes = 60
        });
        var tokenService = new TokenService(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "admin@example.com",
            Email = "admin@example.com",
            DisplayName = "Admin"
        };

        var issued = tokenService.CreateToken(user, ["Admin"]);

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "kirichess",
            ValidateAudience = true,
            ValidAudience = "kirichess",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("unit-test-signing-key-at-least-32-bytes-long")),
            RoleClaimType = ClaimTypes.Role
        };

        var principal = handler.ValidateToken(issued.Value, validationParameters, out _);

        Assert.True(principal.IsInRole("Admin"));
    }

    /// <summary>
    /// Phase 9d.2: proves the "mfa" claim round trip end to end, mirroring the role-claim test above.
    /// The claim is deliberately NOT "amr" (the JWT-standard name) - see TwoFactorClaims's doc comment
    /// for why JwtSecurityTokenHandler's default inbound map would silently swallow that name. This
    /// test is what catches someone "standardising" it back to "amr".
    /// </summary>
    [Fact]
    public void CreateToken_for_a_two_factor_enabled_user_carries_the_mfa_claim()
    {
        var options = Options.Create(new JwtOptions
        {
            Key = "unit-test-signing-key-at-least-32-bytes-long",
            Issuer = "kirichess",
            Audience = "kirichess",
            ExpiryMinutes = 60
        });
        var tokenService = new TokenService(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "twofactor@example.com",
            Email = "twofactor@example.com",
            DisplayName = "Two Factor",
            TwoFactorEnabled = true
        };

        var issued = tokenService.CreateToken(user);

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "kirichess",
            ValidateAudience = true,
            ValidAudience = "kirichess",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes("unit-test-signing-key-at-least-32-bytes-long")),
            RoleClaimType = ClaimTypes.Role
        };

        var principal = handler.ValidateToken(issued.Value, validationParameters, out _);

        Assert.Equal("true", principal.FindFirstValue(TwoFactorClaims.ClaimType));
    }

    [Fact]
    public void CreateToken_for_a_user_without_two_factor_carries_no_mfa_claim()
    {
        var options = Options.Create(new JwtOptions
        {
            Key = "unit-test-signing-key-at-least-32-bytes-long",
            Issuer = "kirichess",
            Audience = "kirichess",
            ExpiryMinutes = 60
        });
        var tokenService = new TokenService(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "nofactor@example.com",
            Email = "nofactor@example.com",
            DisplayName = "No Factor",
            TwoFactorEnabled = false
        };

        var issued = tokenService.CreateToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == TwoFactorClaims.ClaimType);
    }
}
