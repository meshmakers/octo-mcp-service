using Meshmakers.Octo.Backend.McpServices.Options;
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
        serviceEnabledKey: null, // auto-enabled for all tenants
        autoEnable: false,
        // AB#4208 — pair with octo-common-services Fix 1+2. Wiring the registry
        // is defensive today (MCP's StartTenantAsync is a no-op so the registry
        // path is unused), but it future-proofs any later override that may
        // throw, ensuring failures survive a pod restart instead of being lost
        // to the in-process pending list alone.
        failedTenantRegistry: failedTenantRegistry
    )
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
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
