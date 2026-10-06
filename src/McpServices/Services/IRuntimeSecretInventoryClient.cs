using Meshmakers.Octo.Backend.McpServices.Models;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Reads the secrets overview of a tenant (<c>secrets { inventory … summary … }</c>, handover §7) from the
///     tenant's runtime GraphQL endpoint (asset-services) with the caller's token. Used by the
///     <c>get_secret_inventory</c> MCP tool (AB#5543). The endpoint never returns secret values; this client only
///     selects slot metadata.
/// </summary>
public interface IRuntimeSecretInventoryClient
{
    /// <summary>
    ///     Runs the inventory query. Returns a typed outcome — the caller never sees an exception.
    /// </summary>
    /// <param name="accessToken">Bearer token to authenticate against asset-services.</param>
    /// <param name="tenantId">Tenant whose secrets overview to read.</param>
    /// <param name="query">Filters, paging and optional parts of the query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SecretInventoryQueryResult> QueryAsync(
        string accessToken,
        string tenantId,
        SecretInventoryRequest query,
        CancellationToken cancellationToken = default);
}

/// <summary>Arguments of <see cref="IRuntimeSecretInventoryClient.QueryAsync" />.</summary>
public sealed class SecretInventoryRequest
{
    /// <summary>Page size.</summary>
    public int First { get; init; } = 50;

    /// <summary>Cursor of the last item of the previous page.</summary>
    public string? After { get; init; }

    /// <summary>Only entities of this CK type (derived types included).</summary>
    public string? CkTypeId { get; init; }

    /// <summary>Only slots in one of these storage forms (GraphQL enum names, e.g. <c>ENC_V2</c>).</summary>
    public IReadOnlyList<string>? Forms { get; init; }

    /// <summary>true: only re-entry tasks; false: only slots without.</summary>
    public bool? NeedsReEntry { get; init; }

    /// <summary>Free-text search over rtId, CK type, well-known name, display name and attribute path.</summary>
    public string? Search { get; init; }

    /// <summary>Also select <c>summary</c>.</summary>
    public bool IncludeSummary { get; init; }

    /// <summary>Also select <c>usedBy</c> per item (pipeline usages; heavier).</summary>
    public bool IncludeUsedBy { get; init; }
}

/// <summary>Typed outcome of <see cref="IRuntimeSecretInventoryClient.QueryAsync" />.</summary>
public enum SecretInventoryQueryOutcome
{
    /// <summary>The query returned data.</summary>
    Succeeded,

    /// <summary>The caller lacks the AdminPanelManagement role (GraphQL error code <c>Forbidden</c>).</summary>
    Forbidden,

    /// <summary>HTTP 401 or 403 — token rejected.</summary>
    Unauthorised,

    /// <summary>Network failure, timeout, or HTTP error status from asset-services.</summary>
    NotReachable,

    /// <summary>GraphQL errors or an unexpected response body.</summary>
    UnexpectedError
}

/// <summary>Result envelope of <see cref="IRuntimeSecretInventoryClient.QueryAsync" />.</summary>
public sealed class SecretInventoryQueryResult
{
    /// <summary>What happened.</summary>
    public required SecretInventoryQueryOutcome Outcome { get; init; }

    /// <summary>The inventory page on <see cref="SecretInventoryQueryOutcome.Succeeded" />.</summary>
    public SecretInventoryConnectionInfo? Inventory { get; init; }

    /// <summary>The summary when requested and <see cref="SecretInventoryQueryOutcome.Succeeded" />.</summary>
    public SecretInventorySummaryInfo? Summary { get; init; }

    /// <summary>Human-readable error message when <see cref="Outcome" /> != Succeeded.</summary>
    public string? ErrorMessage { get; init; }
}
