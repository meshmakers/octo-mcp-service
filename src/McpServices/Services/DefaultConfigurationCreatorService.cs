using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

// ReSharper disable once ClassNeverInstantiated.Global
internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<McpServiceOptions> options,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    ISystemContext systemContext,
    FailedTenantRegistry failedTenantRegistry)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        Constants.McpServiceIdentityDataVersionKey,
        Constants.McpServiceIdentityDataVersionValue,
        migrationService: null,
        ckModelUpgradeService: null,
        runtimeRepositoryProvider: null,
        // No per-tenant enablement flag — MCP runs on every tenant the host knows
        // about. Both nulls intentional: serviceEnabledKey=null disables the
        // Enable/Disable/IsEnabled lifecycle (no on-off switch), and autoEnable=false
        // because there is nothing to auto-enable in the first place.
        serviceEnabledKey: null,
        autoEnable: false,
        // AB#4208 — pair with octo-common-services Fix 1+2. Wiring the registry
        // is defensive today (MCP's StartTenantAsync is a no-op so the registry
        // path is unused), but it future-proofs any later override that may
        // throw, ensuring failures survive a pod restart instead of being lost
        // to the in-process pending list alone.
        failedTenantRegistry: failedTenantRegistry
    )
{
    private readonly ISystemContext _systemContext = systemContext;
    private readonly ILogger<DefaultConfigurationCreatorService> _logger = logger;

    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    /// <summary>
    ///     AB#9729: before the system tenant's setup, resets its identity data version when the agent-exchange client
    ///     (secret) changed since it was last registered, so the base class sends the client again. Child tenants receive
    ///     the identity data on every start anyway. See <see cref="AgentExchangeClientRegistration" />.
    /// </summary>
    protected override async Task SetupTenantAsync(string tenantId)
    {
        if (string.Equals(tenantId, _systemContext.TenantId, StringComparison.Ordinal) &&
            await _systemContext.IsSystemTenantExistingAsync())
        {
            if (await AgentExchangeClientRegistration.ResetIdentityDataVersionIfChangedAsync(_systemContext,
                    Constants.McpServiceIdentityDataVersionKey, AgentExchangeClientRegistration.Fingerprint(options.Value)))
            {
                _logger.LogInformation(
                    "Agent-exchange client changed since its last registration; identity data of the system tenant " +
                    "'{TenantId}' is sent again", tenantId);
            }
        }

        if (!AgentExchangeClientRegistration.IsConfigured(options.Value))
        {
            _logger.LogWarning(
                "No agent-exchange secret is configured (Mcp:AgentExchangeClientSecret); tenant '{TenantId}' gets no " +
                "agent-exchange client and every downstream call of an agent is refused", tenantId);
        }

        await base.SetupTenantAsync(tenantId);
    }

    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Scopes are registered centrally by the identity service
    }

    protected override void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // API resources are registered centrally by the identity service
    }

    protected override void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // AB#9729: the confidential agent-exchange client is MCP's own and lives in every tenant (an agent exchange is
        // presented in the agent's own tenant). It is not part of the blueprint and not mirrored, so the
        // AutoProvisionInChildTenants concern below does not apply to it. Null when no secret is configured.
        var agentExchangeClient = AgentExchangeClientRegistration.BuildClient(options.Value);
        if (agentExchangeClient != null)
        {
            createIdentityDataCommandRequest.Clients = [agentExchangeClient];
        }

        // AB#4208 — MCP clients are now seeded by the System.Identity.Bootstrap
        // blueprint (entities 660…33 / 660…34) with AutoProvisionInChildTenants=true
        // and propagated to child tenants by IClientMirrorProvisioningService.
        // Sending them again here would double-write the entity, and because the
        // DistClientDto wire format does not carry AutoProvisionInChildTenants the
        // Identity-side CreateClientIfNotExistAsync would silently reset the flag
        // to false — breaking the mirror flow. Leave empty: the standardized base
        // still emits the command (the version key keeps tracking that MCP has
        // had a chance to set its OIDC data), the consumer iterates an empty
        // Clients list, and the blueprint owns the actual entity state.
    }
}
