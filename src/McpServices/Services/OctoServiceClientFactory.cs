using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Meshmakers.Octo.Sdk.ServiceClient.ReportingServices;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

internal sealed class OctoServiceClientFactory : IOctoServiceClientFactory
{
    private readonly IOptions<OctoServiceUrlOptions> _urlOptions;

    public OctoServiceClientFactory(IOptions<OctoServiceUrlOptions> urlOptions)
    {
        _urlOptions = urlOptions;
    }

    public IAssetServicesClient CreateAssetClient(string tenantId, string accessToken)
    {
        var endpoint = RequireUrl(_urlOptions.Value.AssetServiceUrl, nameof(OctoServiceUrlOptions.AssetServiceUrl));

        var options = new AssetServiceClientOptions
        {
            EndpointUri = endpoint,
            TenantId = tenantId
        };

        return new AssetServicesClient(options, MakeToken(accessToken));
    }

    public IIdentityServicesClient CreateIdentityClient(string tenantId, string accessToken)
    {
        var endpoint = RequireUrl(_urlOptions.Value.IdentityServiceUrl, nameof(OctoServiceUrlOptions.IdentityServiceUrl));

        var options = new IdentityServiceClientOptions
        {
            EndpointUri = endpoint,
            TenantId = tenantId
        };

        return new IdentityServicesClient(options, MakeToken(accessToken));
    }

    public ICommunicationServicesClient CreateCommunicationClient(string? tenantId, string accessToken)
    {
        var endpoint = RequireUrl(_urlOptions.Value.CommunicationServiceUrl,
            nameof(OctoServiceUrlOptions.CommunicationServiceUrl));

        var options = new CommunicationServiceClientOptions
        {
            EndpointUri = endpoint,
            TenantId = tenantId
        };

        return new CommunicationServicesClient(options, MakeToken(accessToken));
    }

    public IStreamDataServicesClient CreateStreamDataClient(string tenantId, string accessToken)
    {
        // StreamData is hosted on the Asset Repository endpoint.
        var endpoint = RequireUrl(_urlOptions.Value.AssetServiceUrl,
            nameof(OctoServiceUrlOptions.AssetServiceUrl));

        var options = new StreamDataServiceClientOptions
        {
            EndpointUri = endpoint,
            TenantId = tenantId
        };

        return new StreamDataServicesClient(options, MakeToken(accessToken));
    }

    public IReportingServicesClient CreateReportingClient(string? tenantId, string accessToken)
    {
        var endpoint = RequireUrl(_urlOptions.Value.ReportingServiceUrl,
            nameof(OctoServiceUrlOptions.ReportingServiceUrl));

        var options = new ReportingServicesClientOptions
        {
            EndpointUri = endpoint,
            TenantId = tenantId
        };

        return new ReportingServicesClient(options, MakeToken(accessToken));
    }

    public IBotServicesClient CreateBotClient(string accessToken)
    {
        var endpoint = RequireUrl(_urlOptions.Value.BotServiceUrl,
            nameof(OctoServiceUrlOptions.BotServiceUrl));

        var options = new BotServiceClientOptions { EndpointUri = endpoint };
        return new BotServicesClient(options, MakeToken(accessToken));
    }

    private static ServiceClientAccessToken MakeToken(string accessToken) =>
        new() { AccessToken = accessToken };

    private static string RequireUrl(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ServiceConfigurationMissingException(
                $"OctoServiceUrls:{propertyName} is not configured. Add it to appsettings.json.");
        }

        return value;
    }
}
