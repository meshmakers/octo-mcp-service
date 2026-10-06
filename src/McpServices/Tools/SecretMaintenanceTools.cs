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
        SecretSweepModeDto.ClearUnknownKid
    ];

    /// <summary>Returns the last secret sweep report of a tenant, or of every tenant.</summary>
    [McpServerTool(Name = "get_secret_status")]
    [Description(
        "Show the encryption status of Secret attributes from the last secret sweep report (bot service): counts " +
        "per form (notSet, placeholder, plaintext, encV1, encV2 per key id, unknown key id, failed), per CK type and " +
        "attribute path, the active key id, strict-mode flags and the list of secrets to re-enter after a " +
        "cross-environment restore. Never contains secret values. Default: the tenant's report; allTenants=true " +
        "returns the last report of every tenant (system tenant only). Equivalent to octo-cli SecretStatus.")]
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

            var report = await bot.Client!.GetSecretSweepReportAsync(bot.TenantId!);
            if (report == null)
            {
                return new SecretStatusResponse
                {
                    IsSuccess = true,
                    TenantId = bot.TenantId,
                    Message = $"No secret sweep report for tenant '{bot.TenantId}' yet. Run start_secret_sweep " +
                              "with mode Verify."
                };
            }

            var summary = Summarise(report);
            return new SecretStatusResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                Report = report,
                Summaries = [summary],
                Message = $"Last sweep ({report.Mode}, {report.Trigger}) of tenant '{report.TenantId}' completed " +
                          $"{report.CompletedAt:O} with outcome {report.Outcome}; plaintext=" +
                          $"{summary.Totals?.Plaintext ?? 0}, legacy={report.RemainingLegacyValues}, " +
                          $"secrets to re-enter={summary.SecretsToReEnterCount}."
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
        "Modes: Verify (read-only, counts forms; no confirm needed), Encrypt (encrypts remaining plaintext / enc:v1 " +
        "values and normalises placeholders), Reprotect (re-encrypts everything with the active key, after a key " +
        "rotation), ClearUnknownKid (clears secrets whose key id is unknown — IRREVERSIBLE, the values must be " +
        "re-entered). Writing modes take a pre-sweep backup in the bot service and require confirm=true. Decrypt is " +
        "not available. Optionally waits for completion and returns the report. Equivalent to octo-cli " +
        "ReprotectSecrets.")]
    public static async Task<SecretSweepResponse> StartSecretSweep(
        McpServer server,
        [Description("Sweep mode: Verify (default), Encrypt, Reprotect or ClearUnknownKid.")]
        string mode = "Verify",
        [Description("When true, sweep all tenants (system API, requires system tenant rights).")]
        bool allTenants = false,
        [Description("Must be true for the writing modes Encrypt, Reprotect and ClearUnknownKid.")]
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
                               (sweepMode == SecretSweepModeDto.ClearUnknownKid
                                   ? " ClearUnknownKid permanently clears secrets encrypted with an unknown key id."
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
                ? await bot.Client!.StartSecretSweepAllTenantsAsync(sweepMode)
                : await bot.Client!.StartSecretSweepAsync(bot.TenantId!, sweepMode);
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
            error = $"Unknown secret sweep mode '{value}'. Use Verify, Encrypt, Reprotect or ClearUnknownKid.";
            return false;
        }

        sweepMode = Enum.Parse<SecretSweepModeDto>(match);
        if (!OfferedModes.Contains(sweepMode))
        {
            error = $"Secret sweep mode '{match}' is not available through MCP (it would write clear text back). " +
                    "Use Verify, Encrypt, Reprotect or ClearUnknownKid.";
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
            SecretsToReEnterCount = report.SecretsToReEnter.Count
        };
    }
}
