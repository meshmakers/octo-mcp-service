using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>
///     AB#9729: MCP registers its confidential agent-exchange client <see cref="Constants.AgentExchangeClientId" /> in every
///     tenant over the bus (<see cref="DefaultConfigurationCreatorService" />), with the secret of
///     <see cref="McpServiceOptions.AgentExchangeClientSecret" />. Identity admits an agent exchange only when this client,
///     confidential and with the token-exchange grant as its only grant type, presents it in the agent's own tenant.
/// </summary>
/// <remarks>
///     <para>
///         Same delivery scheme as the AI service's MCP service client (octo-ai-services
///         <c>McpServiceClientRegistration</c>, AB#9188): child tenants receive the identity data on every start; the system
///         tenant only while its stored identity data version is below the expected one. So that a secret set after the
///         first deploy, or a rotated one, reaches the system tenant without a code change, a fingerprint of client id,
///         scope and secret is kept in the system tenant configuration (<see cref="FingerprintKey" />); when it differs,
///         the stored version is reset and the base class sends the identity data again. The fingerprint is the first 8
///         hex characters of a SHA-256 and cannot be turned back into the secret.
///     </para>
///     <para>
///         Without a secret nothing is registered, and an exchange client registered earlier keeps its old secret, which
///         MCP no longer knows: every downstream call of an agent is refused (fail closed).
///     </para>
/// </remarks>
internal static class AgentExchangeClientRegistration
{
    /// <summary>System tenant configuration key of the fingerprint of the last registered agent-exchange client.</summary>
    internal const string FingerprintKey = "McpAgentExchangeClientFingerprint";

    /// <summary>Fingerprint stored while no secret is configured (no client registered).</summary>
    internal const string NotConfiguredFingerprint = "none";

    /// <summary>The token-exchange grant, the only grant type of the client.</summary>
    internal const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>The one scope the client may request: the platform scope of the exchanged token.</summary>
    internal const string Scope = "octo_api";

    /// <summary>True when a secret is configured; only then is the client registered and an agent token exchanged.</summary>
    internal static bool IsConfigured(McpServiceOptions options) =>
        !string.IsNullOrWhiteSpace(options.AgentExchangeClientSecret);

    /// <summary>The client to register in a tenant, or <c>null</c> when no secret is configured.</summary>
    internal static DistClientDto? BuildClient(McpServiceOptions options)
    {
        if (!IsConfigured(options))
        {
            return null;
        }

        return new DistClientDto(Constants.AgentExchangeClientId,
            "MCP service: exchanges agent tokens (octo_agent) for platform tokens", options.PublicUrl)
        {
            AllowedGrantTypes = [TokenExchangeGrantType],
            RequireClientSecret = true,
            // Plaintext over the bus; identity stores only its hash.
            ClientSecret = options.AgentExchangeClientSecret,
            RequireConsent = false,
            AllowOfflineAccess = false,
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            AllowedCorsOrigins = [],
            AllowedScopes = [Scope]
        };
    }

    /// <summary>
    ///     Fingerprint of the configured client: the first 8 hex characters of SHA-256 over client id, scope and secret;
    ///     <see cref="NotConfiguredFingerprint" /> when no secret is configured.
    /// </summary>
    internal static string Fingerprint(McpServiceOptions options)
    {
        if (!IsConfigured(options))
        {
            return NotConfiguredFingerprint;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Constants.AgentExchangeClientId}\n{Scope}\n{options.AgentExchangeClientSecret}"));
        return Convert.ToHexStringLower(hash)[..8];
    }

    /// <summary>
    ///     When the stored fingerprint differs from <paramref name="fingerprint" />, resets the stored identity data version
    ///     of the system tenant and stores the new fingerprint in one transaction. Returns <c>true</c> when it reset.
    /// </summary>
    internal static async Task<bool> ResetIdentityDataVersionIfChangedAsync(ISystemContext systemContext,
        string identityDataVersionKey, string fingerprint)
    {
        using var session = await systemContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        var open = true;
        try
        {
            var stored = await systemContext.GetConfigurationAsync(session, FingerprintKey).ConfigureAwait(false);
            if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                open = false;
                await session.AbortTransactionAsync().ConfigureAwait(false);
                return false;
            }

            await systemContext.SetConfigurationAsync(session, identityDataVersionKey,
                new DefaultConfigurationVersion { Version = -1 }).ConfigureAwait(false);
            await systemContext.SetConfigurationAsync(session, FingerprintKey, fingerprint).ConfigureAwait(false);
            open = false;
            await session.CommitTransactionAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (open)
            {
                await session.AbortTransactionAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}
