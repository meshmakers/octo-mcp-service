using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Secret maintenance tools (AB#5543, concept §5.2): read the secret sweep report of a tenant or of all
///     tenants and start a sweep job in the bot service. Mirrors octo-cli <c>SecretStatus</c> /
///     <c>ReprotectSecrets</c>. Nothing here ever returns or accepts a secret value; the emergency
///     <c>Decrypt</c> mode is deliberately not offered.
/// </summary>
[McpServerToolType]
public sealed class SecretMaintenanceTools
{
    private static readonly SecretSweepModeDto[] OfferedModes =
    [
        SecretSweepModeDto.Verify, SecretSweepModeDto.Encrypt, SecretSweepModeDto.Reprotect,
        SecretSweepModeDto.CleanupUnreadable
    ];

    private const string ModeList = "Verify, Encrypt, Reprotect or CleanupUnreadable";

    /// <summary>Number of recent sweep runs returned by <c>get_secret_status</c> in tenant mode.</summary>
    internal const int RecentRunLimit = 10;

    /// <summary>Returns the last secret sweep report of a tenant, or of every tenant.</summary>
    [McpServerTool(Name = "get_secret_status")]
    [Description(
        "Show the encryption status of Secret attributes (bot service). Tenant mode returns the environment status " +
        "(key ring configured, active and known key ids, legacy v1 key, strict mode and since when, recurring " +
        "Verify cron, last Verify of the tenant, requiredKeyIds = key ids the encrypted dumps need, warnings such as " +
        "DumpKeyMissing when one of them is not in the key ring), the recent sweep runs (mode, trigger, outcome, totals, " +
        "placeholdersNormalized, unreadable count and the state of the pre-sweep dump) and the last sweep report: " +
        "counts per form (notSet, plaintext, encV1, encV2 per key id, unknown key id, failed), per CK type and " +
        "attribute path, and the list of unreadable secrets (stored, but the key id is not in the key ring — e.g. " +
        "after a restore from another environment; they must be re-entered or removed with the CleanupUnreadable " +
        "sweep). Never contains secret values. allTenants=true returns the last report of every tenant (system " +
        "tenant only). Equivalent to octo-cli SecretStatus.")]
    public static async Task<SecretStatusResponse> GetSecretStatus(
        McpServer server,
        [Description("When true, return the last report of every tenant (system API, requires system tenant rights).")]
        bool allTenants = false,
        [Description("Tenant to report on. Falls back to URL route. Ignored when allTenants=true.")]
        string? tenantId = null)
    {
        var bot = allTenants
            ? await BotClientContext.TryBuildSystemAsync(server)
            : await BotClientContext.TryBuildAsync(server, tenantId);
        if (bot.Error != null)
        {
            return new SecretStatusResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            if (allTenants)
            {
                var reports = (await bot.Client!.GetSecretSweepReportsAsync()).ToList();
                var summaries = reports.Select(Summarise).ToList();
                return new SecretStatusResponse
                {
                    IsSuccess = true,
                    Reports = reports,
                    Summaries = summaries,
                    Message = reports.Count == 0
                        ? "No secret sweep reports yet. Run start_secret_sweep with mode Verify."
                        : $"{reports.Count} tenant report(s); plaintext={summaries.Sum(s => s.Totals?.Plaintext ?? 0)}, " +
                          $"legacy={summaries.Sum(s => s.RemainingLegacyValues)}, " +
                          $"strict-mode violations={summaries.Count(s => s.StrictModeViolation)}, " +
                          $"secrets to re-enter={summaries.Sum(s => s.SecretsToReEnterCount)}."
                };
            }

            var environment = await bot.Client!.GetSecretEnvironmentStatusAsync(bot.TenantId!);
            var runs = (await bot.Client.GetSecretSweepRunsAsync(bot.TenantId!, RecentRunLimit)).ToList();
            var report = await bot.Client.GetSecretSweepReportAsync(bot.TenantId!);
            var environmentText = DescribeEnvironment(environment);
            if (report == null)
            {
                return new SecretStatusResponse
                {
                    IsSuccess = true,
                    TenantId = bot.TenantId,
                    Environment = environment,
                    RecentRuns = runs,
                    Message = $"{environmentText} No secret sweep report for tenant '{bot.TenantId}' yet. Run " +
                              "start_secret_sweep with mode Verify."
                };
            }

            var summary = Summarise(report);
            return new SecretStatusResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                Environment = environment,
                RecentRuns = runs,
                Report = report,
                Summaries = [summary],
                Message = $"{environmentText} Last sweep ({report.Mode}, {report.Trigger}) of tenant " +
                          $"'{report.TenantId}' completed {report.CompletedAt:O} with outcome {report.Outcome}; " +
                          $"plaintext={summary.Totals?.Plaintext ?? 0}, legacy={report.RemainingLegacyValues}, " +
                          $"unreadable (re-entry needed)={summary.UnreadableCount}, " +
                          $"placeholders normalized={report.PlaceholdersNormalized}; {runs.Count} recent run(s)."
            };
        }
        catch (Exception ex)
        {
            return new SecretStatusResponse { IsSuccess = false, ErrorMessage = ex.Message, TenantId = bot.TenantId };
        }
    }

    /// <summary>Default page size of <c>get_secret_inventory</c>.</summary>
    internal const int InventoryDefaultPageSize = 50;

    /// <summary>Largest page size <c>get_secret_inventory</c> accepts.</summary>
    internal const int InventoryMaxPageSize = 200;

    private static readonly IReadOnlyDictionary<string, string> StorageForms =
        new[] { "NOT_SET", "PLAINTEXT", "ENC_V1", "ENC_V2", "KEY_MISSING", "CORRUPT" }
            .ToDictionary(f => f.Replace("_", string.Empty), f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists the secret slots of a tenant (secrets overview, handover §7). Read-only, never values.</summary>
    [McpServerTool(Name = "get_secret_inventory")]
    [Description(
        "List the Secret attribute slots of a tenant from the asset repository's secrets overview: per slot the " +
        "entity (ckTypeId, rtId, rtWellKnownName, displayName), attributePath (camelCase, record members like " +
        "endpoints[key=prod].token), attributeName, required, storage form (NOT_SET, PLAINTEXT, ENC_V1, ENC_V2, " +
        "KEY_MISSING, CORRUPT), keyId, setAt and needsReEntry (KEY_MISSING, CORRUPT, or NOT_SET and required). " +
        "needsReEntry=true lists the live re-entry tasks (e.g. after a restore from another environment). " +
        "summary=true adds the counts per form and per key id; usedBy=true adds the pipelines (RevealSecret@1 " +
        "nodes) that reveal each secret (heavier). Paged: pass endCursor as after while hasNextPage is true. " +
        "Requires the AdminPanelManagement role in the tenant. Read-only; secret values are never returned.")]
    public static async Task<SecretInventoryResponse> GetSecretInventory(
        McpServer server,
        [Description("Only entities of this CK type, e.g. 'System.Communication/Application' (derived types included).")]
        string? ckTypeId = null,
        [Description("Only slots in one of these storage forms: NOT_SET, PLAINTEXT, ENC_V1, ENC_V2, KEY_MISSING, CORRUPT.")]
        string[]? forms = null,
        [Description("true: only re-entry tasks; false: only slots that need no re-entry; omit for all.")]
        bool? needsReEntry = null,
        [Description("Free-text search over rtId, CK type (full or short name), well-known name, display name and attribute path.")]
        string? search = null,
        [Description("Page size (default 50, max 200).")]
        int first = InventoryDefaultPageSize,
        [Description("Cursor of the previous page (its endCursor).")]
        string? after = null,
        [Description("When true, also return the summary (counts per storage form and per key id).")]
        bool summary = false,
        [Description("When true, also return the pipelines that reveal each secret (heavier query).")]
        bool usedBy = false,
        [Description("Tenant to list. Falls back to URL route.")]
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        if (first < 1 || first > InventoryMaxPageSize)
        {
            return new SecretInventoryResponse
            {
                IsSuccess = false,
                ErrorMessage = $"first must be between 1 and {InventoryMaxPageSize}."
            };
        }

        var normalizedForms = new List<string>();
        foreach (var form in forms ?? [])
        {
            if (string.IsNullOrWhiteSpace(form) ||
                !StorageForms.TryGetValue(form.Trim().Replace("_", string.Empty), out var normalized))
            {
                return new SecretInventoryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Unknown storage form '{form}'. Use NOT_SET, PLAINTEXT, ENC_V1, ENC_V2, " +
                                   "KEY_MISSING or CORRUPT."
                };
            }

            if (!normalizedForms.Contains(normalized))
            {
                normalizedForms.Add(normalized);
            }
        }

        string resolvedTenantId;
        try
        {
            resolvedTenantId = server.Services!.GetRequiredService<ITenantResolutionService>().ResolveTenantId(tenantId);
        }
        catch (Exception ex)
        {
            return new SecretInventoryResponse { IsSuccess = false, ErrorMessage = $"Failed to resolve tenant: {ex.Message}" };
        }

        // Tenant-aware token (home tenant → session token; other tenant → exchanged token), as AssetClientContext.
        var token = await McpSessionContext.ResolveAccessTokenAsync(server, resolvedTenantId);
        if (token.Error != null || token.AccessToken == null)
        {
            return new SecretInventoryResponse
            {
                IsSuccess = false,
                TenantId = resolvedTenantId,
                ErrorMessage = token.Error ?? Constants.NotAuthenticatedError
            };
        }

        try
        {
            var client = server.Services!.GetRequiredService<IRuntimeSecretInventoryClient>();
            var result = await client.QueryAsync(token.AccessToken, resolvedTenantId, new SecretInventoryRequest
            {
                First = first,
                After = string.IsNullOrWhiteSpace(after) ? null : after.Trim(),
                CkTypeId = string.IsNullOrWhiteSpace(ckTypeId) ? null : ckTypeId.Trim(),
                Forms = normalizedForms.Count > 0 ? normalizedForms : null,
                NeedsReEntry = needsReEntry,
                Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                IncludeSummary = summary,
                IncludeUsedBy = usedBy
            }, cancellationToken);

            if (result.Outcome != SecretInventoryQueryOutcome.Succeeded || result.Inventory == null)
            {
                return new SecretInventoryResponse
                {
                    IsSuccess = false,
                    TenantId = resolvedTenantId,
                    ErrorMessage = result.ErrorMessage ?? "The secret inventory query returned no data."
                };
            }

            var inventory = result.Inventory;
            var reEntryOnPage = inventory.Items.Count(i => i.NeedsReEntry);
            return new SecretInventoryResponse
            {
                IsSuccess = true,
                TenantId = resolvedTenantId,
                TotalCount = inventory.TotalCount,
                HasNextPage = inventory.PageInfo?.HasNextPage ?? false,
                EndCursor = inventory.PageInfo?.EndCursor,
                Items = inventory.Items,
                Summary = result.Summary,
                Message = $"{inventory.Items.Count} of {inventory.TotalCount} secret slot(s) in tenant " +
                          $"'{resolvedTenantId}'; {reEntryOnPage} on this page need re-entry." +
                          (result.Summary != null
                              ? $" Summary: total={result.Summary.Total}, encV2={result.Summary.EncV2}, " +
                                $"plaintext={result.Summary.Plaintext}, encV1={result.Summary.EncV1}, " +
                                $"keyMissing={result.Summary.KeyMissing}, corrupt={result.Summary.Corrupt}, " +
                                $"notSet={result.Summary.NotSet}, needsReEntry={result.Summary.NeedsReEntry}."
                              : string.Empty) +
                          (inventory.PageInfo?.HasNextPage == true
                              ? $" More pages: pass after='{inventory.PageInfo.EndCursor}'."
                              : string.Empty)
            };
        }
        catch (Exception ex)
        {
            return new SecretInventoryResponse { IsSuccess = false, TenantId = resolvedTenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Starts a secret sweep job for a tenant or all tenants.</summary>
    [McpServerTool(Name = "start_secret_sweep")]
    // Static classification: the registry has no per-argument risk, and three of the four modes rewrite or clear
    // stored secrets. Verify is read-only and runs without confirm; the writing modes also need confirm=true.
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Start a secret sweep job in the bot service over all Secret attributes of a tenant (or all tenants). " +
        "Modes: Verify (read-only, counts forms and lists unreadable secrets; no confirm needed), Encrypt (encrypts " +
        "remaining plaintext / enc:v1 values; legacy stored placeholder strings become not set once), Reprotect " +
        "(re-encrypts everything with the active key, after a key rotation or after adding a source environment's " +
        "key), CleanupUnreadable (removes secrets whose key id is not in the key ring — IRREVERSIBLE except via the " +
        "pre-sweep dump, the values must be re-entered). Writing modes take a pre-sweep dump in the bot service and " +
        "require confirm=true (passed on to the bot service). Decrypt is not available. Optionally waits for " +
        "completion and returns the report. Equivalent to octo-cli ReprotectSecrets.")]
    public static async Task<SecretSweepResponse> StartSecretSweep(
        McpServer server,
        [Description("Sweep mode: Verify (default), Encrypt, Reprotect or CleanupUnreadable.")]
        string mode = "Verify",
        [Description("When true, sweep all tenants (system API, requires system tenant rights).")]
        bool allTenants = false,
        [Description("Must be true for the writing modes Encrypt, Reprotect and CleanupUnreadable.")]
        bool confirm = false,
        [Description("When true, wait for the job to finish and return the resulting report(s).")]
        bool waitForCompletion = false,
        [Description("Wait timeout in minutes when waitForCompletion=true (default 30).")]
        int waitTimeoutMinutes = 30,
        [Description("Tenant to sweep. Falls back to URL route. Ignored when allTenants=true.")]
        string? tenantId = null)
    {
        if (!TryParseMode(mode, out var sweepMode, out var modeError))
        {
            return new SecretSweepResponse { IsSuccess = false, ErrorMessage = modeError, AllTenants = allTenants };
        }

        var scope = allTenants ? "all tenants" : $"tenant '{tenantId ?? "(route)"}'";
        if (sweepMode != SecretSweepModeDto.Verify && !confirm)
        {
            return new SecretSweepResponse
            {
                IsSuccess = false,
                Mode = sweepMode,
                AllTenants = allTenants,
                ErrorMessage = $"Refusing to start a {sweepMode} secret sweep for {scope} without confirm=true." +
                               (sweepMode == SecretSweepModeDto.CleanupUnreadable
                                   ? " CleanupUnreadable permanently removes secrets whose key id is not in the key " +
                                     "ring (recoverable only from the pre-sweep dump)."
                                   : string.Empty)
            };
        }

        if (waitTimeoutMinutes <= 0)
        {
            return new SecretSweepResponse
            {
                IsSuccess = false, Mode = sweepMode, AllTenants = allTenants,
                ErrorMessage = "waitTimeoutMinutes must be > 0."
            };
        }

        var bot = allTenants
            ? await BotClientContext.TryBuildSystemAsync(server)
            : await BotClientContext.TryBuildAsync(server, tenantId);
        if (bot.Error != null)
        {
            return new SecretSweepResponse
            {
                IsSuccess = false, Mode = sweepMode, AllTenants = allTenants, ErrorMessage = bot.Error
            };
        }

        string? jobId = null;
        try
        {
            var job = allTenants
                ? await bot.Client!.StartSecretSweepAllTenantsAsync(sweepMode, confirm)
                : await bot.Client!.StartSecretSweepAsync(bot.TenantId!, sweepMode, confirm);
            jobId = job.JobId;

            if (!waitForCompletion)
            {
                return new SecretSweepResponse
                {
                    IsSuccess = true,
                    TenantId = bot.TenantId,
                    AllTenants = allTenants,
                    Mode = sweepMode,
                    JobId = jobId,
                    Message = $"{sweepMode} secret sweep for {(allTenants ? "all tenants" : $"tenant '{bot.TenantId}'")} " +
                              $"started (job '{jobId}'). Call get_secret_status after the job completed."
                };
            }

            await JobPollingHelper.WaitForJobAsync(bot.Client, jobId, TimeSpan.FromMinutes(waitTimeoutMinutes));

            var response = new SecretSweepResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                AllTenants = allTenants,
                Mode = sweepMode,
                JobId = jobId
            };
            if (allTenants)
            {
                response.Reports = (await bot.Client.GetSecretSweepReportsAsync()).ToList();
                response.Message = $"{sweepMode} secret sweep for all tenants completed (job '{jobId}'); " +
                                   $"{response.Reports.Count} tenant report(s).";
            }
            else
            {
                response.Report = await bot.Client.GetSecretSweepReportAsync(bot.TenantId!);
                response.Message = $"{sweepMode} secret sweep for tenant '{bot.TenantId}' completed (job '{jobId}')" +
                                   (response.Report != null ? $" with outcome {response.Report.Outcome}." : ".");
            }

            return response;
        }
        catch (Exception ex)
        {
            return new SecretSweepResponse
            {
                IsSuccess = false,
                TenantId = bot.TenantId,
                AllTenants = allTenants,
                Mode = sweepMode,
                JobId = jobId,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>Restores the pre-sweep dump of a sweep run into the same tenant (AB#5559).</summary>
    [McpServerTool(Name = "restore_secret_sweep_dump")]
    // Replaces the whole tenant database and can bring plaintext secrets back.
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Restore the pre-sweep dump of a secret sweep run into the tenant it was taken from (bot service). " +
        "DESTRUCTIVE: the tenant's database is dropped and replaced by the dump (all changes since the run are " +
        "lost); a Verify sweep runs afterwards. WARNING: a dump taken before the first Encrypt sweep contains " +
        "PLAINTEXT secrets — restoring it brings the plaintext back; run start_secret_sweep with mode Encrypt " +
        "(confirm=true) right after the restore. Find the run id with get_secret_status (recentRuns[].runId, " +
        "dump.exists). Requires the SecretManagement role in the tenant and confirm=true. Refused when the dump " +
        "was deleted or expired (DumpDeleted), no longer exists (not found), or is encrypted with a key id that " +
        "is not in the key ring (DumpKeyMissing — put the key back first). Returns the restore job id; " +
        "optionally waits for completion. Never returns secret values.")]
    public static async Task<SecretSweepDumpRestoreResponse> RestoreSecretSweepDump(
        McpServer server,
        [Description("Tenant the dump was taken from (a dump can only be restored into it). Required.")]
        string tenantId,
        [Description("Run id of the sweep run whose pre-sweep dump is restored (get_secret_status recentRuns[].runId). Required.")]
        string runId,
        [Description("Must be true: the restore replaces the tenant's data and may bring back plaintext secrets.")]
        bool confirm,
        [Description("When true, wait for the restore job to finish.")]
        bool waitForCompletion = false,
        [Description("Wait timeout in minutes when waitForCompletion=true (default 60).")]
        int waitTimeoutMinutes = 60)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return new SecretSweepDumpRestoreResponse { IsSuccess = false, ErrorMessage = "tenantId is required." };
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false, TenantId = tenantId, ErrorMessage = "runId is required."
            };
        }

        if (!confirm)
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false,
                TenantId = tenantId,
                RunId = runId,
                ErrorMessage = $"Refusing to restore the pre-sweep dump of run '{runId}' into tenant '{tenantId}' " +
                               "without confirm=true. The restore replaces the tenant's database; a dump taken " +
                               "before the first Encrypt brings plaintext secrets back (run Encrypt afterwards)."
            };
        }

        if (waitTimeoutMinutes <= 0)
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false, TenantId = tenantId, RunId = runId,
                ErrorMessage = "waitTimeoutMinutes must be > 0."
            };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId.Trim());
        if (bot.Error != null)
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false, TenantId = tenantId, RunId = runId, ErrorMessage = bot.Error
            };
        }

        string? jobId = null;
        try
        {
            var job = await bot.Client!.RestoreSecretSweepDumpAsync(bot.TenantId!, runId.Trim(), true);
            jobId = job.JobId;
            var followUp = " Then run start_secret_sweep with mode Encrypt (confirm=true) if the dump predates the " +
                           "first Encrypt, and check get_secret_status.";

            if (!waitForCompletion)
            {
                return new SecretSweepDumpRestoreResponse
                {
                    IsSuccess = true,
                    TenantId = bot.TenantId,
                    RunId = runId,
                    JobId = jobId,
                    Message = $"Restore of the pre-sweep dump of run '{runId}' into tenant '{bot.TenantId}' started " +
                              $"(job '{jobId}'). Wait for the job to complete.{followUp}"
                };
            }

            await JobPollingHelper.WaitForJobAsync(bot.Client, jobId, TimeSpan.FromMinutes(waitTimeoutMinutes));
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                RunId = runId,
                JobId = jobId,
                Completed = true,
                Message = $"Pre-sweep dump of run '{runId}' restored into tenant '{bot.TenantId}' (job '{jobId}'); " +
                          $"a Verify sweep ran afterwards.{followUp}"
            };
        }
        catch (SecretSweepDumpRestoreException ex)
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false,
                TenantId = bot.TenantId,
                RunId = runId,
                RefusalReason = ex.Reason.ToString(),
                ErrorMessage = ex.Reason switch
                {
                    SecretSweepDumpRestoreFailure.DumpKeyMissing =>
                        "DumpKeyMissing: the dump is encrypted with a key id that is not in the key ring " +
                        "(see get_secret_status environment.requiredKeyIds). Put the key back into the key ring first.",
                    SecretSweepDumpRestoreFailure.DumpDeleted =>
                        "DumpDeleted: the pre-sweep dump of this run was deleted (early or expired).",
                    SecretSweepDumpRestoreFailure.NotFound =>
                        "Not found: unknown run, the run has no pre-sweep dump, or the dump is no longer stored.",
                    _ => ex.Message
                }
            };
        }
        catch (Exception ex)
        {
            return new SecretSweepDumpRestoreResponse
            {
                IsSuccess = false, TenantId = bot.TenantId, RunId = runId, JobId = jobId, ErrorMessage = ex.Message
            };
        }
    }

    internal static bool TryParseMode(string? mode, out SecretSweepModeDto sweepMode, out string? error)
    {
        sweepMode = SecretSweepModeDto.Verify;
        error = null;
        var value = string.IsNullOrWhiteSpace(mode) ? nameof(SecretSweepModeDto.Verify) : mode.Trim();

        // Names only — Enum.TryParse would also accept numbers such as "4" (= Decrypt).
        var match = Enum.GetNames<SecretSweepModeDto>()
            .FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            error = $"Unknown secret sweep mode '{value}'. Use {ModeList}.";
            return false;
        }

        sweepMode = Enum.Parse<SecretSweepModeDto>(match);
        if (!OfferedModes.Contains(sweepMode))
        {
            error = $"Secret sweep mode '{match}' is not available through MCP (it would write clear text back). " +
                    $"Use {ModeList}.";
            return false;
        }

        return true;
    }

    private static SecretStatusSummary Summarise(SecretSweepReportDto report)
    {
        return new SecretStatusSummary
        {
            TenantId = report.TenantId,
            Mode = report.Mode,
            Trigger = report.Trigger,
            Outcome = report.Outcome,
            CompletedAt = report.CompletedAt,
            ActiveKeyId = report.ActiveKeyId,
            StrictModeActive = report.StrictModeActive,
            StrictModeViolation = report.StrictModeViolation,
            RemainingLegacyValues = report.RemainingLegacyValues,
            Totals = report.Steps.LastOrDefault()?.Totals,
            SecretsToReEnterCount = report.SecretsToReEnter.Count,
            PlaceholdersNormalized = report.PlaceholdersNormalized,
            UnreadableCount = report.Unreadable.Count,
            Unreadable = report.Unreadable
        };
    }

    private static string DescribeEnvironment(SecretEnvironmentStatusDto environment)
    {
        if (!environment.KeyRingConfigured)
        {
            return "Key ring NOT configured (secret writes fail with SecretEncryptionNotConfigured)." +
                   DescribeDumpKeys(environment);
        }

        return $"Key ring configured: active key '{environment.ActiveKeyId}', known keys " +
               $"[{string.Join(", ", environment.KnownKeyIds)}], strict mode " +
               $"{(environment.StrictMode ? "on" : "off")}" +
               (environment.StrictModeSince is { } since ? $" since {since:O}" : string.Empty) +
               $", last Verify {(environment.LastVerifyAt is { } at ? at.ToString("O") : "never")}." +
               DescribeDumpKeys(environment);
    }

    /// <summary>
    ///     Key ids the encrypted dumps need (AB#5559) and the <c>DumpKeyMissing</c> warning, if any.
    /// </summary>
    internal static string DescribeDumpKeys(SecretEnvironmentStatusDto environment)
    {
        var text = environment.RequiredKeyIds.Count > 0
            ? $" Encrypted dumps need key ids [{string.Join(", ", environment.RequiredKeyIds)}]."
            : string.Empty;
        if (environment.Warnings.Contains(SecretEnvironmentWarningCodes.DumpKeyMissing))
        {
            var missing = environment.RequiredKeyIds.Where(k => !environment.KnownKeyIds.Contains(k)).ToList();
            text += " WARNING DumpKeyMissing: an encrypted dump needs a key id that is not in the key ring" +
                    (missing.Count > 0 ? $" ([{string.Join(", ", missing)}])" : string.Empty) +
                    "; it can neither be restored nor downloaded until the key is put back.";
        }

        return text;
    }
}
