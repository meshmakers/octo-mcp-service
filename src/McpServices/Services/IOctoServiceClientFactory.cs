using Meshmakers.Octo.Sdk.ServiceClient.AdminPanel.System;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Meshmakers.Octo.Sdk.ServiceClient.ReportingServices;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Constructs SDK service-client proxies on demand, scoped to a specific tenant and authenticated with the
///     access token of the current MCP session. Mirrors what the CLI sets up once at startup, but per-request.
/// </summary>
public interface IOctoServiceClientFactory
{
    /// <summary>
    ///     Builds a fresh <see cref="IAssetServicesClient" /> bound to the given parent tenant and access token.
    ///     The Asset service requires <see cref="AssetServiceClientOptions.TenantId" /> on every request, so a new
    ///     client is constructed per tenant.
    /// </summary>
    /// <param name="tenantId">Parent tenant whose child tenants this client manages.</param>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IAssetServicesClient CreateAssetClient(string tenantId, string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="IIdentityServicesClient" /> bound to the given tenant and access token.
    ///     The Identity service routes per-tenant under <c>{tenantId}/v1</c>.
    /// </summary>
    /// <param name="tenantId">Tenant to scope identity operations to.</param>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IIdentityServicesClient CreateIdentityClient(string tenantId, string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="ICommunicationServicesClient" /> bound to the given tenant and access token.
    ///     The Communication Controller routes per-tenant under <c>{tenantId}/v1</c>; pass <c>null</c> for
    ///     <paramref name="tenantId"/> to route to <c>system/v1</c>.
    /// </summary>
    /// <param name="tenantId">Tenant to scope communication operations to, or <c>null</c> for system scope.</param>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    ICommunicationServicesClient CreateCommunicationClient(string? tenantId, string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="IStreamDataServicesClient" /> bound to the given parent tenant and access
    ///     token. Backed by the asset-repository endpoint (StreamData lives there).
    /// </summary>
    /// <param name="tenantId">Parent tenant whose stream data lifecycle is managed.</param>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IStreamDataServicesClient CreateStreamDataClient(string tenantId, string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="IReportingServicesClient" /> bound to the given tenant and access token.
    ///     Pass <c>null</c> for <paramref name="tenantId"/> to fall back to system scope.
    /// </summary>
    /// <param name="tenantId">Tenant to scope reporting operations to, or <c>null</c> for system scope.</param>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IReportingServicesClient CreateReportingClient(string? tenantId, string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="IBotServicesClient" /> authenticated with the given access token. The Bot
    ///     service is system-scoped (no tenant routing).
    /// </summary>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IBotServicesClient CreateBotClient(string accessToken);

    /// <summary>
    ///     Builds a fresh <see cref="IAdminPanelClient" /> authenticated with the given access token. The Admin
    ///     Panel client is system-scoped (no tenant routing).
    /// </summary>
    /// <param name="accessToken">OAuth access token for the bearer header.</param>
    IAdminPanelClient CreateAdminPanelClient(string accessToken);
}
