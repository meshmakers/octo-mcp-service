using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace McpServices.Tests;

/// <summary>
///     Builds unsigned JWTs for tests. <see cref="JwtSecurityTokenHandler.ReadJwtToken" /> (used by the
///     production JWT-claim readers) does not verify signatures, so an unsigned token with the desired
///     claims is enough to exercise <c>tenant_id</c> / <c>role</c> decoding.
/// </summary>
internal static class TestJwt
{
    /// <summary>
    ///     Creates a JWT string carrying the given <c>tenant_id</c> and <c>role</c> claims.
    /// </summary>
    public static string Create(string? tenantId = null, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var token = new JwtSecurityToken(claims: claims);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
