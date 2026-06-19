using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
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
        createIdentityDataCommandRequest.Clients = new List<DistClientDto>
        {
            // Swagger UI client (Authorization Code Flow)
            new(Constants.McpServicesSwaggerClientId,
                "OctoMesh MCP Services Swagger Client",
                options.Value.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    options.Value.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],

                PostLogoutRedirectUris = [options.Value.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [options.Value.PublicUrl.TrimEnd('/')],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.OctoApiFullAccess
                ]
            },

            // MCP Device Authorization Flow client (for CLI/AI clients like Claude Code)
            new(Constants.McpServicesDeviceClientId,
                "OctoMesh MCP Services Device Client",
                options.Value.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.DeviceCode],
                AllowOfflineAccess = true,
                RedirectUris = [],
                PostLogoutRedirectUris = [],
                AllowedCorsOrigins = [],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.Scopes.OfflineAccess
                ]
            }
        };
    }
}
