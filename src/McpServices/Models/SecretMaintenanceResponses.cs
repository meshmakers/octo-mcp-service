using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response of <c>get_secret_status</c> (AB#5543). Carries the bot service's secret sweep report(s) as-is;
///     the reports contain counts, key ids and entity references only — never secret values.
/// </summary>
public class SecretStatusResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the report belongs to (tenant mode), <c>null</c> for the system-wide view.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     Environment-level encryption status (key ring, strict mode, recurring Verify, this tenant's last Verify);
    ///     tenant mode only.
    /// </summary>
    public SecretEnvironmentStatusDto? Environment { get; set; }

    /// <summary>Recent sweep runs of the tenant, newest first, including the pre-sweep dump state; tenant mode only.</summary>
    public List<SecretSweepRunDto>? RecentRuns { get; set; }

    /// <summary>Last sweep report of the tenant (tenant mode); <c>null</c> when no sweep has run yet.</summary>
    public SecretSweepReportDto? Report { get; set; }

    /// <summary>Last sweep report per tenant (system-wide mode).</summary>
    public List<SecretSweepReportDto>? Reports { get; set; }

    /// <summary>One compact line per report, for a quick overview.</summary>
    public List<SecretStatusSummary> Summaries { get; set; } = [];
}

/// <summary>Compact view of one <see cref="SecretSweepReportDto" />.</summary>
public class SecretStatusSummary
{
    /// <summary>Tenant id.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Sweep mode of the report.</summary>
    public SecretSweepModeDto Mode { get; set; }

    /// <summary>What started the sweep (Manual, Recurring, Restore).</summary>
    public SecretSweepTriggerDto Trigger { get; set; }

    /// <summary>Outcome of the sweep.</summary>
    public SecretSweepOutcomeDto Outcome { get; set; }

    /// <summary>Completion time (UTC).</summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>Active key id of the key ring when the sweep ran.</summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>Whether strict mode was active.</summary>
    public bool StrictModeActive { get; set; }

    /// <summary>Whether legacy values were found while strict mode was active.</summary>
    public bool StrictModeViolation { get; set; }

    /// <summary>Remaining legacy (plaintext / enc:v1) values.</summary>
    public long RemainingLegacyValues { get; set; }

    /// <summary>Counts per form of the last step (totals over all slots).</summary>
    public SecretFormCountsReportDto? Totals { get; set; }

    /// <summary>Number of secrets cleared by a CleanupUnreadable sweep (they must be re-entered).</summary>
    public int SecretsToReEnterCount { get; set; }

    /// <summary>Legacy stored placeholder strings converted once to "not set" (migration only).</summary>
    public long PlaceholdersNormalized { get; set; }

    /// <summary>Number of stored secrets whose key id is not in the key ring (re-entry tasks).</summary>
    public int UnreadableCount { get; set; }

    /// <summary>Stored secrets whose key id is not in the key ring (re-entry list; references only, no values).</summary>
    public List<SecretUnreadableValueDto> Unreadable { get; set; } = [];
}

/// <summary>Response of <c>start_secret_sweep</c> (AB#5543).</summary>
public class SecretSweepResponse
{
    /// <summary>True when the job was started (and, when waited for, completed).</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant swept (tenant mode), <c>null</c> for all tenants.</summary>
    public string? TenantId { get; set; }

    /// <summary>True when the sweep spans all tenants.</summary>
    public bool AllTenants { get; set; }

    /// <summary>Sweep mode.</summary>
    public SecretSweepModeDto Mode { get; set; }

    /// <summary>Bot job id; poll with the job status of the bot service.</summary>
    public string? JobId { get; set; }

    /// <summary>The tenant report after completion (only when waited for, tenant mode).</summary>
    public SecretSweepReportDto? Report { get; set; }

    /// <summary>The reports after completion (only when waited for, all tenants).</summary>
    public List<SecretSweepReportDto>? Reports { get; set; }
}
