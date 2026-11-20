using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Identity wave 2: the identity provider methods (<c>IdentityProvider.CreateIdentityProvider-1</c>,
///     <c>UpdateIdentityProvider-1</c>, <c>DeleteIdentityProvider-1</c>) are declared on the abstract
///     <c>System.Identity/IdentityProvider</c> and called on the generated client of the concrete provider type. The
///     polymorphic REST DTO decides which client; its fields become the method parameters.
/// </summary>
internal static class IdentityProviderMethods
{
    /// <summary>Creates the provider described by <paramref name="dto" />; the result is its runtime id.</summary>
    internal static Task<CkMethodResult<string?>> CreateAsync(ITenantClient tenantClient, IdentityProviderDto dto)
    {
        var a = Arguments.From(dto);
        return dto switch
        {
            GoogleIdentityProviderDto => new RtGoogleIdentityProviderMethodClient(tenantClient).CreateIdentityProviderAsync(
                a.Name, a.Enabled, a.Description, a.AllowSelfRegistration, a.DefaultGroupRtId, a.ClientId, a.ClientSecret,
                a.TenantId, a.Authority, a.Host, a.Port, a.UseTls, a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            MicrosoftIdentityProviderDto => new RtMicrosoftIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            FacebookIdentityProviderDto => new RtFacebookIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            AzureEntraIdProviderDto => new RtAzureEntraIdIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            MicrosoftAdProviderDto => new RtMicrosoftAdIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            OpenLdapProviderDto => new RtOpenLdapIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            OctoTenantIdentityProviderDto => new RtOctoTenantIdentityProviderMethodClient(tenantClient)
                .CreateIdentityProviderAsync(a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            _ => throw Unsupported(dto)
        };
    }

    /// <summary>Updates provider <paramref name="rtId" /> to the fields of <paramref name="dto" />.</summary>
    internal static Task<CkMethodResult> UpdateAsync(ITenantClient tenantClient, string rtId, IdentityProviderDto dto)
    {
        var a = Arguments.From(dto);
        return dto switch
        {
            GoogleIdentityProviderDto => new RtGoogleIdentityProviderMethodClient(tenantClient).UpdateIdentityProviderAsync(
                rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration, a.DefaultGroupRtId, a.ClientId,
                a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls, a.UserBaseDn, a.UserNameAttribute,
                a.ParentTenantId),
            MicrosoftIdentityProviderDto => new RtMicrosoftIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            FacebookIdentityProviderDto => new RtFacebookIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            AzureEntraIdProviderDto => new RtAzureEntraIdIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            MicrosoftAdProviderDto => new RtMicrosoftAdIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            OpenLdapProviderDto => new RtOpenLdapIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            OctoTenantIdentityProviderDto => new RtOctoTenantIdentityProviderMethodClient(tenantClient)
                .UpdateIdentityProviderAsync(rtId, a.Name, a.Enabled, a.Description, a.AllowSelfRegistration,
                    a.DefaultGroupRtId, a.ClientId, a.ClientSecret, a.TenantId, a.Authority, a.Host, a.Port, a.UseTls,
                    a.UserBaseDn, a.UserNameAttribute, a.ParentTenantId),
            _ => throw Unsupported(dto)
        };
    }

    /// <summary>Deletes provider <paramref name="rtId" />, whose stored type is the one of <paramref name="existing" />.</summary>
    internal static Task<CkMethodResult> DeleteAsync(ITenantClient tenantClient, string rtId,
        IdentityProviderDto? existing) => existing switch
    {
        GoogleIdentityProviderDto => new RtGoogleIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        MicrosoftIdentityProviderDto =>
            new RtMicrosoftIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        FacebookIdentityProviderDto =>
            new RtFacebookIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        AzureEntraIdProviderDto =>
            new RtAzureEntraIdIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        MicrosoftAdProviderDto =>
            new RtMicrosoftAdIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        OpenLdapProviderDto => new RtOpenLdapIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        OctoTenantIdentityProviderDto =>
            new RtOctoTenantIdentityProviderMethodClient(tenantClient).DeleteIdentityProviderAsync(rtId),
        null => throw new InvalidOperationException($"Identity provider '{rtId}' not found."),
        _ => throw Unsupported(existing)
    };

    private static InvalidOperationException Unsupported(IdentityProviderDto dto) =>
        new($"Unsupported identity provider type '{dto.GetType().Name}'.");

    /// <summary>The method parameters of a provider DTO; fields the provider type does not have stay <c>null</c>.</summary>
    private sealed record Arguments(
        string Name,
        bool? Enabled,
        string? Description,
        bool? AllowSelfRegistration,
        string? DefaultGroupRtId,
        string? ClientId,
        string? ClientSecret,
        string? TenantId,
        string? Authority,
        string? Host,
        int? Port,
        bool? UseTls,
        string? UserBaseDn,
        string? UserNameAttribute,
        string? ParentTenantId)
    {
        internal static Arguments From(IdentityProviderDto dto)
        {
            var a = new Arguments(dto.Name ?? string.Empty, dto.IsEnabled, dto.Description, dto.AllowSelfRegistration,
                dto.DefaultGroupRtId, null, null, null, null, null, null, null, null, null, null);
            return dto switch
            {
                GoogleIdentityProviderDto o => a with { ClientId = o.ClientId, ClientSecret = Secret(o.ClientSecret) },
                MicrosoftIdentityProviderDto o => a with { ClientId = o.ClientId, ClientSecret = Secret(o.ClientSecret) },
                FacebookIdentityProviderDto o => a with { ClientId = o.ClientId, ClientSecret = Secret(o.ClientSecret) },
                AzureEntraIdProviderDto o => a with
                {
                    ClientId = o.ClientId, ClientSecret = Secret(o.ClientSecret), TenantId = o.TenantId,
                    Authority = o.Authority
                },
                MicrosoftAdProviderDto o => a with { Host = o.Host, Port = o.Port, UseTls = o.UseTls },
                OpenLdapProviderDto o => a with
                {
                    Host = o.Host, Port = o.Port, UseTls = o.UseTls, UserBaseDn = o.UserBaseDn,
                    UserNameAttribute = o.UserNameAttribute
                },
                OctoTenantIdentityProviderDto o => a with { ParentTenantId = o.ParentTenantId },
                _ => a
            };
        }

        /// <summary>Never prints the members: <see cref="ClientSecret" /> must not reach a log or an error text.</summary>
        public override string ToString() => nameof(Arguments);

        /// <summary>Write-only: <c>null</c> or empty keeps the stored secret.</summary>
        private static string? Secret(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
