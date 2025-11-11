using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

// ReSharper disable once ClassNeverInstantiated.Global
internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<McpServiceOptions> options,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    ISystemContext systemContext)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        Constants.McpServiceIdentityDataVersionKey,
        Constants.McpServiceIdentityDataVersionValue // we don't need migrations in this service
        )
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }



    //
    // protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    // {
    //     createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
    //     {
    //         new(CommonConstants.CommunicationSystemApiFullAccess,
    //             CommonConstants.CommunicationSystemApiFullAccessDisplayName),
    //         new(CommonConstants.CommunicationTenantApiFullAccess,
    //             CommonConstants.CommunicationTenantApiFullAccessDisplayName),
    //         new(CommonConstants.CommunicationTenantApiReadOnly,
    //             CommonConstants.CommunicationTenantApiReadOnlyDisplayName),
    //     };
    // }
    //
    // protected override  void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    // {
    //     createIdentityDataCommandRequest.ApiResources = new List<DistApiResourcesDto>
    //     {
    //         new(CommonConstants.CommunicationSystemApi, CommonConstants.CommunicationSystemApiDisplayName)
    //         {
    //             Description = CommonConstants.CommunicationSystemApiDescription,
    //             IsEnabled = true,
    //             Scopes = new List<string>
    //             {
    //                 CommonConstants.CommunicationSystemApiFullAccess,
    //             }
    //         },
    //         new(CommonConstants.CommunicationTenantApi, CommonConstants.CommunicationTenantApiDisplayName)
    //         {
    //             Description = CommonConstants.CommunicationTenantApiDescription,
    //             IsEnabled = true,
    //             Scopes = new List<string>
    //             {
    //                 CommonConstants.CommunicationTenantApiReadOnly,
    //                 CommonConstants.CommunicationTenantApiFullAccess
    //             }
    //         }
    //     };
    // }
    //
    // protected override  void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    // {
    //     createIdentityDataCommandRequest.Clients = new List<DistClientDto>
    //     {
    //         new(CommonConstants.CommunicationControllerServicesSwaggerClientId,
    //             CommunicationControllerTexts.SwaggerClient_Description,
    //             options.Value.PublicUrl)
    //         {
    //             AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],
    //
    //             RedirectUris =
    //             [
    //                 options.Value.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
    //             ],
    //
    //             PostLogoutRedirectUris = [options.Value.PublicUrl.EnsureEndsWith("/")],
    //             AllowedCorsOrigins = [options.Value.PublicUrl.TrimEnd('/')],
    //             AllowedScopes =
    //             [
    //                 CommonConstants.Scopes.OpenId,
    //                 CommonConstants.Scopes.Profile,
    //                 CommonConstants.Scopes.Email,
    //                 JwtClaimTypes.Role,
    //                 CommonConstants.CommunicationSystemApiFullAccess,
    //                 CommonConstants.CommunicationTenantApiReadOnly,
    //                 CommonConstants.CommunicationTenantApiFullAccess
    //             ]
    //         }
    //     };
    // }
}