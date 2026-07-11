using System.IdentityModel.Tokens.Jwt;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Small helper for reading claims out of a bearer access token without validating its signature.
///     Lifted from the duplicated <see cref="System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler" />
///     <c>ReadJwtToken</c> pattern that <c>IdentityTools</c> (whoami / auth_status) and now the
///     cross-tenant switch path (AB#4338) share — the CLAUDE.md rule is to centralise once a third call
///     site appears. Signature verification already happened at the transport gate; here we only need to
///     read <c>tenant_id</c> / <c>role</c> claims for display and routing.
/// </summary>
internal static class JwtClaimReader
{
    /// <summary>
    ///     Reads the <c>tenant_id</c> (or legacy <c>tenant</c>) claim from an access token, or null when
    ///     the token is not a parseable JWT or carries no such claim.
    /// </summary>
    public static string? TryReadTenantId(string accessToken)
    {
        return TryReadSingleClaim(accessToken, "tenant_id") ?? TryReadSingleClaim(accessToken, "tenant");
    }

    /// <summary>
    ///     Reads all <c>role</c> claim values from an access token. Returns an empty list when the token
    ///     is not a parseable JWT or carries no role claims.
    /// </summary>
    public static List<string> ReadRoles(string accessToken)
    {
        var jwt = TryRead(accessToken);
        if (jwt == null)
        {
            return [];
        }

        return jwt.Claims
            .Where(c => c.Type == "role")
            .Select(c => c.Value)
            .Distinct()
            .ToList();
    }

    private static string? TryReadSingleClaim(string accessToken, string claimType)
    {
        var jwt = TryRead(accessToken);
        return jwt?.Claims.FirstOrDefault(c => c.Type == claimType)?.Value;
    }

    private static JwtSecurityToken? TryRead(string accessToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.CanReadToken(accessToken) ? handler.ReadJwtToken(accessToken) : null;
        }
        catch
        {
            // Opaque (non-JWT) bearer — no claims to read.
            return null;
        }
    }
}
