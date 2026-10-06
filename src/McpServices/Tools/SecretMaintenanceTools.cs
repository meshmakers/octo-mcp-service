using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
        "Verify cron, last Verify of the tenant), the recent sweep runs (mode, trigger, outcome, totals, " +
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
            return "Key ring NOT configured (secret writes fail with SecretEncryptionNotConfigured).";
        }

        return $"Key ring configured: active key '{environment.ActiveKeyId}', known keys " +
               $"[{string.Join(", ", environment.KnownKeyIds)}], strict mode " +
               $"{(environment.StrictMode ? "on" : "off")}" +
               (environment.StrictModeSince is { } since ? $" since {since:O}" : string.Empty) +
               $", last Verify {(environment.LastVerifyAt is { } at ? at.ToString("O") : "never")}.";
    }
}
