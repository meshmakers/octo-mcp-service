using System.ComponentModel;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     OAuth client management tools (Identity service). Mirrors octo-cli Clients commands incl. multi-tenant
///     ClientCredentials mirroring.
/// </summary>
[McpServerToolType]
public sealed class ClientManagementTools
{
    /// <summary>List all OAuth clients in the tenant.</summary>
    [McpServerTool(Name = "get_clients")]
    [Description("List all OAuth clients in the tenant. Equivalent to octo-cli GetClients.")]
    public static async Task<GetClientsResponse> GetClients(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetClientsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var clients = (await ctx.Client!.GetClients()).ToList();
            return new GetClientsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Clients = clients,
                TotalCount = clients.Count,
                Message = clients.Count == 0 ? "No clients found." : $"Found {clients.Count} client(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetClientsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get a single OAuth client by ID.</summary>
    [McpServerTool(Name = "get_client")]
    [Description("Get a single OAuth client by client ID. Equivalent to octo-cli GetClient.")]
    public static async Task<ClientResponse> GetClient(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var client = await ctx.Client!.GetClient(clientId);
            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Client = client
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a new ClientCredentials OAuth client.</summary>
    [McpServerTool(Name = "add_client_credentials_client")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a new OAuth client with grant type 'client_credentials'. Equivalent to octo-cli " +
        "AddClientCredentialsClient. Set autoProvisionInChildTenants=true to mirror this client into every new " +
        "sub-tenant (useful for CI/CD).")]
    public static async Task<ClientResponse> AddClientCredentialsClient(
        McpServer server,
        [Description("Unique client ID.")] string clientId,
        [Description("Display name of the client.")] string clientName,
        [Description("Client secret.")] string clientSecret,
        [Description("If true, the client is auto-provisioned into every new sub-tenant.")] bool autoProvisionInChildTenants = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var validation = ValidateAddClient(clientId, clientName, clientSecret);
        if (validation != null)
        {
            return validation;
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = new ClientDto
            {
                IsEnabled = true,
                ClientId = clientId,
                ClientName = clientName,
                ClientSecret = clientSecret,
                RequireClientSecret = true,
                AllowedGrantTypes = [OidcConstants.GrantTypes.ClientCredentials],
                AllowedScopes = [CommonConstants.OctoApiFullAccess],
                IsOfflineAccessEnabled = true,
                AutoProvisionInChildTenants = autoProvisionInChildTenants ? true : null
            };

            await ctx.Client!.CreateClient(dto);

            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = autoProvisionInChildTenants
                    ? $"ClientCredentials client '{clientId}' created (AutoProvisionInChildTenants enabled)."
                    : $"ClientCredentials client '{clientId}' created."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a new DeviceCode OAuth client.</summary>
    [McpServerTool(Name = "add_device_code_client")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create a new OAuth client with grant type 'device_code'. Equivalent to octo-cli AddDeviceCodeClient.")]
    public static async Task<ClientResponse> AddDeviceCodeClient(
        McpServer server,
        [Description("Unique client ID.")] string clientId,
        [Description("Display name of the client.")] string clientName,
        [Description("Client secret.")] string clientSecret,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var validation = ValidateAddClient(clientId, clientName, clientSecret);
        if (validation != null)
        {
            return validation;
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateClient(new ClientDto
            {
                IsEnabled = true,
                ClientId = clientId,
                ClientName = clientName,
                ClientSecret = clientSecret,
                AllowedGrantTypes = [OidcConstants.GrantTypes.DeviceCode],
                AllowedScopes = [CommonConstants.OctoApiFullAccess],
                IsOfflineAccessEnabled = true
            });

            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = $"DeviceCode client '{clientId}' created."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a new AuthorizationCode OAuth client.</summary>
    [McpServerTool(Name = "add_authorization_code_client")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a new OAuth client with grant type 'authorization_code'. Equivalent to octo-cli " +
        "AddAuthorizationCodeClient. Defaults RedirectUris and PostLogoutRedirectUris to the clientUri unless " +
        "explicit values are passed.")]
    public static async Task<ClientResponse> AddAuthorizationCodeClient(
        McpServer server,
        [Description("Unique client ID.")] string clientId,
        [Description("Display name of the client.")] string clientName,
        [Description("Base URI of the client (used for default redirect/cors/logout URIs).")] string clientUri,
        [Description("Optional explicit redirect URI (defaults to clientUri).")] string? redirectUri = null,
        [Description("Optional front-channel logout URI for Single Logout.")] string? frontChannelLogoutUri = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientName) ||
            string.IsNullOrWhiteSpace(clientUri))
        {
            return new ClientResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId, clientName and clientUri are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = new ClientDto
            {
                IsEnabled = true,
                ClientId = clientId,
                ClientName = clientName,
                ClientUri = clientUri,
                AllowedCorsOrigins = [clientUri.TrimEnd('/')],
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],
                AllowedScopes = [CommonConstants.OctoApiFullAccess],
                PostLogoutRedirectUris = [clientUri.EnsureEndsWith("/")],
                RedirectUris = string.IsNullOrWhiteSpace(redirectUri)
                    ? [clientUri.EnsureEndsWith("/")]
                    : [redirectUri],
                IsOfflineAccessEnabled = true,
                FrontChannelLogoutUri = string.IsNullOrWhiteSpace(frontChannelLogoutUri) ? null : frontChannelLogoutUri,
                FrontChannelLogoutSessionRequired = string.IsNullOrWhiteSpace(frontChannelLogoutUri) ? null : true
            };

            await ctx.Client!.CreateClient(dto);

            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = $"AuthorizationCode client '{clientId}' (URI '{clientUri}') created."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an OAuth client. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_client")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Delete an OAuth client. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteClient.")]
    public static async Task<ClientResponse> DeleteClient(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        if (!confirm)
        {
            return new ClientResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete client '{clientId}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteClient(clientId);
            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = $"Client '{clientId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>List child tenants a flagged ClientCredentials client has been mirrored into.</summary>
    [McpServerTool(Name = "get_client_mirrors")]
    [Description(
        "List the child tenants a flagged ClientCredentials client has been mirrored into. Equivalent to " +
        "octo-cli GetClientMirrors.")]
    public static async Task<GetClientMirrorsResponse> GetClientMirrors(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Parent tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new GetClientMirrorsResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetClientMirrorsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var mirrors = (await ctx.Client!.GetClientMirrors(clientId)).ToList();
            return new GetClientMirrorsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Mirrors = mirrors,
                TotalCount = mirrors.Count,
                Message = mirrors.Count == 0
                    ? $"Client '{clientId}' has no mirrors."
                    : $"Client '{clientId}' is mirrored into {mirrors.Count} sub-tenant(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetClientMirrorsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Back-fill a flagged client into every existing sub-tenant.</summary>
    [McpServerTool(Name = "provision_client_in_existing_tenants")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Back-fill a flagged ClientCredentials client into every existing sub-tenant (idempotent). Equivalent " +
        "to octo-cli ProvisionClientInExistingTenants.")]
    public static async Task<ClientMirrorBackfillResponse> ProvisionClientInExistingTenants(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Parent tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientMirrorBackfillResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientMirrorBackfillResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ProvisionClientInExistingTenants(clientId);
            return new ClientMirrorBackfillResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Backfill = result,
                Message = $"Client '{clientId}' back-filled into existing sub-tenants."
            };
        }
        catch (Exception ex)
        {
            return new ClientMirrorBackfillResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Manually provision a flagged client into a specific sub-tenant.</summary>
    [McpServerTool(Name = "provision_client_in_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Manually provision a ClientCredentials client into a single named sub-tenant. Equivalent to octo-cli " +
        "ProvisionClientInTenant.")]
    public static async Task<ClientMirrorProvisionResponse> ProvisionClientInTenant(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Target sub-tenant ID.")] string childTenantId,
        [Description("Parent tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(childTenantId))
        {
            return new ClientMirrorProvisionResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and childTenantId are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientMirrorProvisionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ProvisionClientInTenant(clientId, childTenantId);
            return new ClientMirrorProvisionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                ChildTenantId = childTenantId,
                Provision = result,
                Message = $"Client '{clientId}' provisioned into sub-tenant '{childTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new ClientMirrorProvisionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Remove a client mirror from a sub-tenant. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "unprovision_client_from_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Remove a client mirror from a sub-tenant. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "UnprovisionClientFromTenant.")]
    public static async Task<ClientMirrorProvisionResponse> UnprovisionClientFromTenant(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Target sub-tenant ID.")] string childTenantId,
        [Description("Must be true to actually remove.")] bool confirm = false,
        [Description("Parent tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(childTenantId))
        {
            return new ClientMirrorProvisionResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and childTenantId are required."
            };
        }

        if (!confirm)
        {
            return new ClientMirrorProvisionResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to unprovision client '{clientId}' from sub-tenant '{childTenantId}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientMirrorProvisionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UnprovisionClientFromTenant(clientId, childTenantId);
            return new ClientMirrorProvisionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                ChildTenantId = childTenantId,
                Message = $"Client '{clientId}' unprovisioned from sub-tenant '{childTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new ClientMirrorProvisionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Toggle the AutoProvisionInChildTenants flag on an existing client.</summary>
    [McpServerTool(Name = "set_client_auto_provision")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Toggle AutoProvisionInChildTenants on an existing client. Note: flipping true does NOT back-fill — " +
        "call provision_client_in_existing_tenants for that. Equivalent to octo-cli SetClientAutoProvision.")]
    public static async Task<ClientResponse> SetClientAutoProvision(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Target value of the AutoProvisionInChildTenants flag.")] bool enabled,
        [Description("Parent tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.SetClientAutoProvisionInChildTenants(clientId, enabled);
            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = enabled
                    ? $"AutoProvisionInChildTenants enabled on '{clientId}'."
                    : $"AutoProvisionInChildTenants disabled on '{clientId}'."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static ClientResponse? ValidateAddClient(string clientId, string clientName, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientName) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return new ClientResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId, clientName and clientSecret are required."
            };
        }

        return null;
    }

    /// <summary>Grant a scope to a client by reading the current client and writing back the extended scope list.</summary>
    [McpServerTool(Name = "add_scope_to_client")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Grant a scope to a client. Reads the current client, appends the scope to AllowedScopes, and writes " +
        "back via UpdateClient. Equivalent to octo-cli AddScopeToClient.")]
    public static async Task<ClientResponse> AddScopeToClient(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Scope name to grant (e.g. 'octo_api').")] string scopeName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(scopeName))
        {
            return new ClientResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and scopeName are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var existing = await ctx.Client!.GetClient(clientId);
            var newScopes = (existing.AllowedScopes ?? []).Append(scopeName).ToList();

            await ctx.Client.UpdateClient(clientId, new ClientDto { AllowedScopes = newScopes });

            return new ClientResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = clientId,
                Message = $"Scope '{scopeName}' granted to client '{clientId}'."
            };
        }
        catch (Exception ex)
        {
            return new ClientResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
