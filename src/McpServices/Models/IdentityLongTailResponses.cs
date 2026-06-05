using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for identity-long-tail tool responses (API resources/scopes/secrets, etc.).</summary>
public class IdentityLongTailResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>List of API resources response.</summary>
public class GetApiResourcesResponse : IdentityLongTailResponse
{
    /// <summary>API resources.</summary>
    public List<ApiResourceDto> ApiResources { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single API-resource response.</summary>
public class ApiResourceResponse : IdentityLongTailResponse
{
    /// <summary>Name of the API resource that was operated on.</summary>
    public string? Name { get; set; }
}

/// <summary>List of API scopes response.</summary>
public class GetApiScopesResponse : IdentityLongTailResponse
{
    /// <summary>API scopes.</summary>
    public List<ApiScopeDto> ApiScopes { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single API-scope response.</summary>
public class ApiScopeResponse : IdentityLongTailResponse
{
    /// <summary>Name of the API scope that was operated on.</summary>
    public string? Name { get; set; }
}

/// <summary>List of API secrets response.</summary>
public class GetApiSecretsResponse : IdentityLongTailResponse
{
    /// <summary>API secrets.</summary>
    public List<ApiSecretDto> ApiSecrets { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }

    /// <summary>Owner identifier (client id or api-resource name) the secrets belong to.</summary>
    public string? OwnerId { get; set; }
}

/// <summary>Single API-secret response.</summary>
public class ApiSecretResponse : IdentityLongTailResponse
{
    /// <summary>Owner identifier (client id or api-resource name).</summary>
    public string? OwnerId { get; set; }

    /// <summary>The secret value the operation targeted (when applicable).</summary>
    public string? SecretValue { get; set; }
}

/// <summary>List of email-domain group rules response.</summary>
public class GetEmailDomainGroupRulesResponse : IdentityLongTailResponse
{
    /// <summary>Rules.</summary>
    public List<EmailDomainGroupRuleDto> Rules { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single email-domain group rule response.</summary>
public class EmailDomainGroupRuleResponse : IdentityLongTailResponse
{
    /// <summary>Runtime id of the rule that was operated on.</summary>
    public string? RtId { get; set; }

    /// <summary>Full rule payload when applicable (Get operations).</summary>
    public EmailDomainGroupRuleDto? Rule { get; set; }
}

/// <summary>List of external-tenant user mappings response.</summary>
public class GetExternalTenantUserMappingsResponse : IdentityLongTailResponse
{
    /// <summary>Mappings.</summary>
    public List<ExternalTenantUserMappingDto> Mappings { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single external-tenant user mapping response.</summary>
public class ExternalTenantUserMappingResponse : IdentityLongTailResponse
{
    /// <summary>Runtime id of the mapping that was operated on.</summary>
    public string? RtId { get; set; }

    /// <summary>Full mapping payload when applicable.</summary>
    public ExternalTenantUserMappingDto? Mapping { get; set; }
}

/// <summary>List of admin-provisioning mappings response.</summary>
public class GetAdminProvisioningMappingsResponse : IdentityLongTailResponse
{
    /// <summary>Target tenant the mappings belong to.</summary>
    public string? TargetTenantId { get; set; }

    /// <summary>Mappings.</summary>
    public List<ExternalTenantUserMappingDto> Mappings { get; set; } = [];

    /// <summary>Total count.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single admin-provisioning response.</summary>
public class AdminProvisioningResponse : IdentityLongTailResponse
{
    /// <summary>Target tenant the operation acted on.</summary>
    public string? TargetTenantId { get; set; }
}
