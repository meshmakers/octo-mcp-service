using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Identity provider management tools (Identity service). Mirrors octo-cli IdentityProviders commands.
///     Note: Update is not exposed in Phase 1 because the polymorphic DTO shape requires a typed merge — see CLI
///     UpdateIdentityProvider. Delete and the 5 typed Add commands are covered.
/// </summary>
[McpServerToolType]
public sealed class IdentityProviderTools
{
    /// <summary>List all identity providers in the tenant.</summary>
    [McpServerTool(Name = "get_identity_providers")]
    [Description("List all identity providers in the tenant. Equivalent to octo-cli GetIdentityProviders.")]
    public static async Task<GetIdentityProvidersResponse> GetIdentityProviders(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetIdentityProvidersResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var providers = (await ctx.Client!.GetIdentityProviders()).ToList();
            return new GetIdentityProvidersResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Providers = providers,
                TotalCount = providers.Count,
                Message = providers.Count == 0
                    ? "No identity providers configured."
                    : $"Found {providers.Count} identity provider(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetIdentityProvidersResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an identity provider. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_identity_provider")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Delete an identity provider by runtime ID. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteIdentityProvider.")]
    public static async Task<IdentityProviderResponse> DeleteIdentityProvider(
        McpServer server,
        [Description("Runtime ID of the identity provider.")] string providerId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = "providerId is required." };
        }

        if (!confirm)
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete identity provider '{providerId}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteIdentityProvider(new OctoObjectId(providerId));
            return new IdentityProviderResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ProviderId = providerId,
                Message = $"Identity provider '{providerId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a Google/Microsoft/Facebook OAuth identity provider.</summary>
    [McpServerTool(Name = "add_oauth_identity_provider")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a Google, Microsoft or Facebook OAuth identity provider. Equivalent to octo-cli AddOAuthIdentityProvider.")]
    public static async Task<IdentityProviderResponse> AddOAuthIdentityProvider(
        McpServer server,
        [Description("Display name (unique).")] string name,
        [Description("Provider type: 'google', 'microsoft' or 'facebook'.")] string providerType,
        [Description("OAuth client ID.")] string clientId,
        [Description("OAuth client secret.")] string clientSecret,
        [Description("Enabled flag.")] bool isEnabled = true,
        [Description("Allow new users to self-register via this provider.")] bool? allowSelfRegistration = null,
        [Description("Optional default group RtId for new users.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "name, clientId and clientSecret are required."
            };
        }

        IdentityProviderDto dto = providerType?.ToLowerInvariant() switch
        {
            "google" => new GoogleIdentityProviderDto
            {
                IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
            },
            "microsoft" => new MicrosoftIdentityProviderDto
            {
                IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
            },
            "facebook" => new FacebookIdentityProviderDto
            {
                IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
            },
            _ => null!
        };

        if (dto == null)
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Unsupported providerType '{providerType}'. Use 'google', 'microsoft' or 'facebook'."
            };
        }

        ApplyOptional(dto, allowSelfRegistration, defaultGroupRtId);

        return await Persist(server, tenantId, dto, name);
    }

    /// <summary>Add an Azure Entra ID identity provider.</summary>
    [McpServerTool(Name = "add_azure_entra_id_identity_provider")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create an Azure Entra ID identity provider. Equivalent to octo-cli AddAzureEntryIdIdentityProvider.")]
    public static async Task<IdentityProviderResponse> AddAzureEntraIdIdentityProvider(
        McpServer server,
        [Description("Display name (unique).")] string name,
        [Description("Azure Entra ID tenant ID.")] string azureTenantId,
        [Description("Azure client ID.")] string clientId,
        [Description("Azure client secret.")] string clientSecret,
        [Description("Enabled flag.")] bool isEnabled = true,
        [Description("Optional authority URL (default: https://login.microsoftonline.com).")] string? authority = null,
        [Description("Allow new users to self-register via this provider.")] bool? allowSelfRegistration = null,
        [Description("Optional default group RtId for new users.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(azureTenantId) ||
            string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "name, azureTenantId, clientId and clientSecret are required."
            };
        }

        var dto = new AzureEntraIdProviderDto
        {
            IsEnabled = isEnabled,
            Name = name,
            TenantId = azureTenantId,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Authority = authority
        };
        ApplyOptional(dto, allowSelfRegistration, defaultGroupRtId);

        return await Persist(server, tenantId, dto, name);
    }

    /// <summary>Add an OpenLDAP identity provider.</summary>
    [McpServerTool(Name = "add_open_ldap_identity_provider")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create an OpenLDAP identity provider. Equivalent to octo-cli AddOpenLdapIdentityProvider.")]
    public static async Task<IdentityProviderResponse> AddOpenLdapIdentityProvider(
        McpServer server,
        [Description("Display name (unique).")] string name,
        [Description("LDAP host.")] string host,
        [Description("User base DN.")] string userBaseDn,
        [Description("LDAP port (default 636).")] ushort port = 636,
        [Description("Username attribute (default 'uid').")] string userNameAttribute = "uid",
        [Description("Use TLS (default true).")] bool useTls = true,
        [Description("Enabled flag.")] bool isEnabled = true,
        [Description("Allow new users to self-register via this provider.")] bool? allowSelfRegistration = null,
        [Description("Optional default group RtId for new users.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(userBaseDn))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "name, host and userBaseDn are required."
            };
        }

        var dto = new OpenLdapProviderDto
        {
            IsEnabled = isEnabled,
            Name = name,
            Host = host,
            Port = port,
            UserBaseDn = userBaseDn,
            UserNameAttribute = userNameAttribute,
            UseTls = useTls
        };
        ApplyOptional(dto, allowSelfRegistration, defaultGroupRtId);

        return await Persist(server, tenantId, dto, name);
    }

    /// <summary>Add a Microsoft Active Directory identity provider.</summary>
    [McpServerTool(Name = "add_active_directory_identity_provider")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a Microsoft Active Directory identity provider. Equivalent to octo-cli " +
        "AddActiveDirectoryIdentityProvider.")]
    public static async Task<IdentityProviderResponse> AddActiveDirectoryIdentityProvider(
        McpServer server,
        [Description("Display name (unique).")] string name,
        [Description("AD host.")] string host,
        [Description("Port (default 636).")] ushort port = 636,
        [Description("Use TLS (default false for AD).")] bool useTls = false,
        [Description("Enabled flag.")] bool isEnabled = true,
        [Description("Allow new users to self-register via this provider.")] bool? allowSelfRegistration = null,
        [Description("Optional default group RtId for new users.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "name and host are required."
            };
        }

        var dto = new MicrosoftAdProviderDto
        {
            IsEnabled = isEnabled,
            Name = name,
            Host = host,
            Port = port,
            UseTls = useTls
        };
        ApplyOptional(dto, allowSelfRegistration, defaultGroupRtId);

        return await Persist(server, tenantId, dto, name);
    }

    /// <summary>Add an OctoTenant identity provider for cross-tenant authentication.</summary>
    [McpServerTool(Name = "add_octo_tenant_identity_provider")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create an OctoTenant identity provider for cross-tenant authentication via a parent tenant. " +
        "Equivalent to octo-cli AddOctoTenantIdentityProvider.")]
    public static async Task<IdentityProviderResponse> AddOctoTenantIdentityProvider(
        McpServer server,
        [Description("Display name (unique).")] string name,
        [Description("Parent tenant ID to authenticate against.")] string parentTenantId,
        [Description("Enabled flag.")] bool isEnabled = true,
        [Description("Allow new users to self-register via this provider.")] bool? allowSelfRegistration = null,
        [Description("Optional default group RtId for new users.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parentTenantId))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "name and parentTenantId are required."
            };
        }

        var dto = new OctoTenantIdentityProviderDto
        {
            IsEnabled = isEnabled,
            Name = name,
            ParentTenantId = parentTenantId
        };
        ApplyOptional(dto, allowSelfRegistration, defaultGroupRtId);

        return await Persist(server, tenantId, dto, name);
    }

    private static void ApplyOptional(IdentityProviderDto dto, bool? allowSelfRegistration, string? defaultGroupRtId)
    {
        if (allowSelfRegistration.HasValue)
        {
            dto.AllowSelfRegistration = allowSelfRegistration.Value;
        }

        if (!string.IsNullOrWhiteSpace(defaultGroupRtId))
        {
            dto.DefaultGroupRtId = defaultGroupRtId;
        }
    }

    private static async Task<IdentityProviderResponse> Persist(
        McpServer server,
        string? tenantId,
        IdentityProviderDto dto,
        string name)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateIdentityProvider(dto);
            return new IdentityProviderResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Identity provider '{name}' created."
            };
        }
        catch (Exception ex)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update common fields on an identity provider — fetches, patches, writes back.</summary>
    [McpServerTool(Name = "update_identity_provider")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Update common fields on an existing identity provider. Fetches the provider, preserves type-specific " +
        "properties (LDAP/AD host+port, Azure tenant+authority, OctoTenant parent), and applies the changes. " +
        "name and isEnabled are required; other fields are optional patches. clientId/clientSecret apply only " +
        "to OAuth-style providers (Google/Microsoft/Facebook/AzureEntraId). Equivalent to octo-cli " +
        "UpdateIdentityProvider.")]
    public static async Task<IdentityProviderResponse> UpdateIdentityProvider(
        McpServer server,
        [Description("Runtime ID of the identity provider.")] string providerId,
        [Description("New display name.")] string name,
        [Description("Enabled flag.")] bool isEnabled,
        [Description("Optional new client ID (OAuth/Azure providers only).")] string? clientId = null,
        [Description("Optional new client secret (OAuth/Azure providers only).")] string? clientSecret = null,
        [Description("Optional new self-registration flag.")] bool? allowSelfRegistration = null,
        [Description("Optional new default group RtId.")] string? defaultGroupRtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(name))
        {
            return new IdentityProviderResponse
            {
                IsSuccess = false,
                ErrorMessage = "providerId and name are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var existing = await ctx.Client!.GetIdentityProvider(new OctoObjectId(providerId));
            if (existing == null)
            {
                return new IdentityProviderResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Identity provider '{providerId}' not found."
                };
            }

            IdentityProviderDto patched = existing switch
            {
                GoogleIdentityProviderDto =>
                    new GoogleIdentityProviderDto
                    {
                        IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
                    },
                MicrosoftIdentityProviderDto =>
                    new MicrosoftIdentityProviderDto
                    {
                        IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
                    },
                FacebookIdentityProviderDto =>
                    new FacebookIdentityProviderDto
                    {
                        IsEnabled = isEnabled, Name = name, ClientId = clientId, ClientSecret = clientSecret
                    },
                AzureEntraIdProviderDto az =>
                    new AzureEntraIdProviderDto
                    {
                        IsEnabled = isEnabled,
                        Name = name,
                        TenantId = az.TenantId,
                        Authority = az.Authority,
                        ClientId = clientId ?? az.ClientId,
                        ClientSecret = clientSecret ?? az.ClientSecret
                    },
                MicrosoftAdProviderDto ad =>
                    new MicrosoftAdProviderDto
                    {
                        IsEnabled = isEnabled, Name = name, Host = ad.Host, Port = ad.Port, UseTls = ad.UseTls
                    },
                OpenLdapProviderDto ldap =>
                    new OpenLdapProviderDto
                    {
                        IsEnabled = isEnabled,
                        Name = name,
                        Host = ldap.Host,
                        Port = ldap.Port,
                        UserBaseDn = ldap.UserBaseDn,
                        UserNameAttribute = ldap.UserNameAttribute,
                        UseTls = ldap.UseTls
                    },
                OctoTenantIdentityProviderDto ot =>
                    new OctoTenantIdentityProviderDto
                    {
                        IsEnabled = isEnabled, Name = name, ParentTenantId = ot.ParentTenantId
                    },
                _ => null!
            };

            if (patched == null)
            {
                return new IdentityProviderResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Unsupported identity provider type for '{providerId}'."
                };
            }

            ApplyOptional(patched, allowSelfRegistration, defaultGroupRtId);

            await ctx.Client.UpdateIdentityProvider(new OctoObjectId(providerId), patched);

            return new IdentityProviderResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ProviderId = providerId,
                Message = $"Identity provider '{providerId}' updated."
            };
        }
        catch (Exception ex)
        {
            return new IdentityProviderResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
