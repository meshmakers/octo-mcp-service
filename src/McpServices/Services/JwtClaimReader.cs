using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

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
    ///     Reads the subject claim from an access token, or null when the token is not a parseable JWT or
    ///     carries no subject. A missing subject is the marker of a client-credentials service token
    ///     (AI Adapter worker, mesh-adapter node) — those act without an end-user identity.
    ///     <para>
    ///     All three spellings the subject can arrive under are probed: the JWT short name <c>sub</c>,
    ///     the JWT short name <c>nameid</c>, and the long SOAP-era type
    ///     <see cref="ClaimTypes.NameIdentifier" /> an issuer may emit when it maps outbound claims.
    ///     Probing only <c>sub</c> would silently classify such a user token as a service token and hand
    ///     it the tenant-gate exemption.
    ///     </para>
    /// </summary>
    public static string? TryReadSubjectId(string accessToken)
    {
        return TryReadSingleClaim(accessToken, "sub")
               ?? TryReadSingleClaim(accessToken, "nameid")
               ?? TryReadSingleClaim(accessToken, ClaimTypes.NameIdentifier);
    }

    /// <summary>
    ///     Reads the <c>client_id</c> claim from an access token, or null when the token is not a
    ///     parseable JWT or carries no client id.
    /// </summary>
    public static string? TryReadClientId(string accessToken)
    {
        return TryReadSingleClaim(accessToken, "client_id");
    }

    /// <summary>
    ///     Reads all role claim values from an access token. Both the JWT short name <c>role</c> and the
    ///     long type <see cref="ClaimTypes.Role" /> are read (an issuer that maps outbound claims emits
    ///     the latter), de-duplicated. Returns an empty list when the token is not a parseable JWT or
    ///     carries no role claims.
    /// </summary>
    public static List<string> ReadRoles(string accessToken)
    {
        var jwt = TryRead(accessToken);
        if (jwt == null)
        {
            return [];
        }

        return jwt.Claims
            .Where(c => c.Type is "role" or ClaimTypes.Role)
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
