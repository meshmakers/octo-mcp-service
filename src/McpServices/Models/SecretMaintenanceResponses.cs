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

/// <summary>Response of <c>restore_secret_sweep_dump</c> (AB#5559). Never contains secret values.</summary>
public class SecretSweepDumpRestoreResponse
{
    /// <summary>True when the restore job was started (and, when waited for, completed).</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the dump is restored into.</summary>
    public string? TenantId { get; set; }

    /// <summary>Run id whose pre-sweep dump is restored.</summary>
    public string? RunId { get; set; }

    /// <summary>Bot job id of the restore.</summary>
    public string? JobId { get; set; }

    /// <summary>True when the tool waited and the job completed.</summary>
    public bool Completed { get; set; }

    /// <summary>
    ///     Why the bot service refused the restore: <c>ConfirmationRequired</c>, <c>NotFound</c>, <c>DumpDeleted</c>
    ///     or <c>DumpKeyMissing</c>; <c>null</c> otherwise.
    /// </summary>
    public string? RefusalReason { get; set; }
}

/// <summary>
///     Response of <c>get_secret_inventory</c> (AB#5543, handover §7): one page of the tenant's secret slots with
///     storage form, key id, set-at and re-entry state, plus the optional summary. Never contains secret values.
/// </summary>
public class SecretInventoryResponse
{
    /// <summary>True when the query succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the inventory belongs to.</summary>
    public string? TenantId { get; set; }

    /// <summary>Number of slots matching the filters (all pages).</summary>
    public int TotalCount { get; set; }

    /// <summary>True when another page follows; pass <see cref="EndCursor" /> as <c>after</c>.</summary>
    public bool HasNextPage { get; set; }

    /// <summary>Cursor of the last item of this page.</summary>
    public string? EndCursor { get; set; }

    /// <summary>The slots of this page.</summary>
    public List<SecretInventoryItemInfo> Items { get; set; } = [];

    /// <summary>Counts per storage form; only when <c>summary=true</c> was requested.</summary>
    public SecretInventorySummaryInfo? Summary { get; set; }
}

/// <summary>Inventory page as returned by the asset repository (<c>SecretInventoryConnection</c>).</summary>
public class SecretInventoryConnectionInfo
{
    /// <summary>Number of slots matching the filters.</summary>
    public int TotalCount { get; set; }

    /// <summary>Paging information.</summary>
    public SecretInventoryPageInfo? PageInfo { get; set; }

    /// <summary>The slots of this page.</summary>
    public List<SecretInventoryItemInfo> Items { get; set; } = [];
}

/// <summary>Paging information of an inventory page.</summary>
public class SecretInventoryPageInfo
{
    /// <summary>True when another page follows.</summary>
    public bool HasNextPage { get; set; }

    /// <summary>Cursor of the last item of the page.</summary>
    public string? EndCursor { get; set; }
}

/// <summary>One secret slot (<c>SecretInventoryItem</c>). Values are never part of it.</summary>
public class SecretInventoryItemInfo
{
    /// <summary>CK type of the entity.</summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>Runtime id of the entity.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>Well-known name of the entity.</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>Display name (stored display name, then string Name attribute, then well-known name); may be null.</summary>
    public string? DisplayName { get; set; }

    /// <summary>camelCase path of the slot, e.g. <c>endpoints[key=prod].token</c>.</summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>CK attribute name (PascalCase) of the top-level attribute.</summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>True when the attribute is required.</summary>
    public bool Required { get; set; }

    /// <summary>Storage form: NOT_SET, PLAINTEXT, ENC_V1, ENC_V2, KEY_MISSING or CORRUPT.</summary>
    public string Form { get; set; } = string.Empty;

    /// <summary>Key id (ENC_V2 / KEY_MISSING only).</summary>
    public string? KeyId { get; set; }

    /// <summary>When the value was set (UTC); null for legacy / not set.</summary>
    public DateTime? SetAt { get; set; }

    /// <summary>True for KEY_MISSING, CORRUPT, or NOT_SET and required: the secret must be re-entered.</summary>
    public bool NeedsReEntry { get; set; }

    /// <summary>Pipelines that reveal this secret; only when <c>usedBy=true</c> was requested.</summary>
    public List<SecretUsageInfo>? UsedBy { get; set; }
}

/// <summary>A <c>RevealSecret@1</c> node that reveals (EXACT) or may reveal (BY_TYPE) a secret slot.</summary>
public class SecretUsageInfo
{
    /// <summary>Data flow of the pipeline, if any.</summary>
    public string? DataFlowRtId { get; set; }

    /// <summary>Name of the data flow.</summary>
    public string? DataFlowName { get; set; }

    /// <summary>The pipeline.</summary>
    public string PipelineRtId { get; set; } = string.Empty;

    /// <summary>Name of the pipeline.</summary>
    public string? PipelineName { get; set; }

    /// <summary>Node position in the pipeline definition.</summary>
    public string NodePath { get; set; } = string.Empty;

    /// <summary>EXACT or BY_TYPE.</summary>
    public string Match { get; set; } = string.Empty;
}

/// <summary>Counts of all secret slots of the tenant per storage form (<c>SecretInventorySummary</c>).</summary>
public class SecretInventorySummaryInfo
{
    /// <summary>All listed slots.</summary>
    public int Total { get; set; }

    /// <summary>Slots without a value.</summary>
    public int NotSet { get; set; }

    /// <summary>Legacy clear-text values still stored.</summary>
    public int Plaintext { get; set; }

    /// <summary>Legacy enc:v1 values.</summary>
    public int EncV1 { get; set; }

    /// <summary>Protected values with a known key id.</summary>
    public int EncV2 { get; set; }

    /// <summary>Protected values whose key id is not in the key ring.</summary>
    public int KeyMissing { get; set; }

    /// <summary>Stored values that cannot be parsed.</summary>
    public int Corrupt { get; set; }

    /// <summary>Re-entry tasks.</summary>
    public int NeedsReEntry { get; set; }

    /// <summary>Protected values per key id.</summary>
    public List<SecretKeyIdCountInfo> EncV2ByKeyId { get; set; } = [];
}

/// <summary>Number of protected values per key id.</summary>
public class SecretKeyIdCountInfo
{
    /// <summary>Key id.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Number of secrets.</summary>
    public int Count { get; set; }
}
