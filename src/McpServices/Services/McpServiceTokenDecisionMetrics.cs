using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Counter <c>octo.tenant_auth.mcp_service_token_decisions</c> (AB#9189): one measurement per tenant decision the
///     MCP tool gate (<see cref="RuntimeSecurityContextResolver" />) takes for a client-credentials token. Tags are
///     low-cardinality only — <c>outcome</c> (<c>allow</c> / <c>deny</c>) and <c>reason</c> (see
///     <see cref="ServiceTokenDecisionReason" />); client id and tenants are in the structured log line, not in the
///     series.
///     <para>
///     The meter is <c>Meshmakers.Octo.TenantAuthorization</c>, which <c>AddObservability()</c> of octo-common-services
///     already exports (it carries <c>octo.tenant_auth.generation_checks</c>), so the counter needs no extra
///     registration.
///     </para>
/// </summary>
internal static class McpServiceTokenDecisionMetrics
{
    /// <summary>Meter name; must equal the string <c>AddObservability()</c> registers.</summary>
    public const string MeterName = "Meshmakers.Octo.TenantAuthorization";

    /// <summary>Counter name.</summary>
    public const string CounterName = "octo.tenant_auth.mcp_service_token_decisions";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>(
        CounterName, "{decision}",
        "Tenant decisions of the MCP tool gate for client-credentials tokens (AB#9189)");

    /// <summary>Records one decision.</summary>
    /// <param name="allowed">Whether the call may act on the target tenant.</param>
    /// <param name="reason">Why, one of the <see cref="ServiceTokenDecisionReason" /> constants.</param>
    public static void Record(bool allowed, string reason)
    {
        Decisions.Add(1,
            new KeyValuePair<string, object?>("outcome", allowed ? "allow" : "deny"),
            new KeyValuePair<string, object?>("reason", reason));
    }
}

/// <summary>Values of the <c>reason</c> tag and log field of a service-token decision (AB#9189).</summary>
internal static class ServiceTokenDecisionReason
{
    /// <summary>The token's <c>tenant_id</c> equals the target tenant.</summary>
    public const string SameTenant = "same_tenant";

    /// <summary>The AI service client of the system tenant — the one cross-tenant pair.</summary>
    public const string AiServiceClientOfSystemTenant = "ai_service_client_of_system_tenant";

    /// <summary>The token was issued for another tenant than the target.</summary>
    public const string TenantMismatch = "tenant_mismatch";

    /// <summary>The token carries no <c>tenant_id</c> claim.</summary>
    public const string NoTenantClaim = "no_tenant_claim";
}
