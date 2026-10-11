using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>
/// Authenticates bearer tokens that start with <see cref="ApiTokenDefaults.Prefix"/> (Phase 9f.5). Returns
/// <see cref="AuthenticateResult.NoResult"/> for anything else so a selector can fall through to JWT.
/// Never stamps <c>role</c> or <c>mfa</c>.
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiTokenService _tokens;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = ReadRawToken();
        if (raw is null) return AuthenticateResult.NoResult();

        var token = await _tokens.FindValidAsync(raw, Context.RequestAborted);
        if (token is null || token.User is null)
            return AuthenticateResult.Fail("Invalid API token.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.UserId.ToString()),
            new("sub", token.UserId.ToString()),
            new("displayName", token.User.DisplayName),
            new(ApiTokenClaims.TokenId, token.Id.ToString()),
            new(ApiTokenClaims.TokenUse, ApiTokenClaims.Api)
        };
        foreach (var scope in ApiTokenService.ScopeList(token))
            claims.Add(new Claim(ApiTokenClaims.Scope, scope));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? ReadRawToken()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var value = header[bearer.Length..].Trim();
            if (value.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal))
                return value;
            return null;
        }

        if (Request.Path.StartsWithSegments("/hubs"))
        {
            var query = Request.Query["access_token"].ToString();
            if (query.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal))
                return query;
        }

        return null;
    }
}
