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
        return CreateFull(tenantId, subjectId: null, clientId: null, roles);
    }

    /// <summary>
    ///     Creates a JWT string carrying <c>tenant_id</c>, <c>sub</c>, <c>client_id</c> and <c>role</c>
    ///     claims. A token WITHOUT <c>sub</c> models a client-credentials service token (AI Adapter
    ///     worker, mesh-adapter) — the class the tenant gate deliberately exempts (AB#5030 / AB#5032).
    /// </summary>
    public static string CreateFull(string? tenantId, string? subjectId, string? clientId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (subjectId != null)
        {
            claims.Add(new Claim("sub", subjectId));
        }

        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var token = new JwtSecurityToken(claims: claims);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
