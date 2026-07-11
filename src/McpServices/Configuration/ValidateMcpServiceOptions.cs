using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     Fails service startup fast when <see cref="McpServiceOptions.PublicUrl" /> is not an absolute
///     http(s) URI. PublicUrl is the RFC 9728 resource identifier the MCP challenge scheme advertises
///     (<see cref="ConfigureMcpAuthenticationOptions" />) and the RFC 8707 resource indicator an
///     interactive client (Claude Code) sends to the token endpoint; the identity blueprint seeds a
///     matching ApiResource from the same value (<c>${octo.mcp.publicUrl}</c>). An unset or relative
///     value would advertise a bogus resource and make Duende reject the token exchange with
///     <c>invalid_target</c> — a silent login break. Refuse to start instead of serving metadata that
///     cannot work.
/// </summary>
internal sealed class ValidateMcpServiceOptions : IValidateOptions<McpServiceOptions>
{
    /// <summary>
    ///     Validates that <see cref="McpServiceOptions.PublicUrl" /> is an absolute http(s) URI.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, McpServiceOptions options)
    {
        if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                $"McpServiceOptions.PublicUrl must be an absolute http(s) URI, but was '{options.PublicUrl}'. "
                + "Set OCTO_MCP__PUBLICURL to the external MCP URL; it must match the identity blueprint's "
                + "${octo.mcp.publicUrl} (the ApiResource an interactive client authenticates against).");
        }

        return ValidateOptionsResult.Success;
    }
}
