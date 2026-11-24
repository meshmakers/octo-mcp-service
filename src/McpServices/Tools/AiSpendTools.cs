using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AiServices;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Response of <c>get_ai_spend</c>.</summary>
public class AiSpendResponse : TenantManagementResponse
{
    /// <summary>Key source, cap and spend of the current and previous UTC month.</summary>
    public AiSpendReportDto? Spend { get; set; }
}

/// <summary>Response of <c>set_ai_spend_cap</c>.</summary>
public class AiSpendCapChangeResponse : TenantManagementResponse
{
    /// <summary>Old and new value of the cap change.</summary>
    public AiSpendCapChangeDto? Change { get; set; }
}

/// <summary>
///     Monthly LLM spend cap of the central-key LLM gateway (AB#8690). Mirrors octo-cli GetAiSpend / SetAiSpendCap. The
///     cap lives in the AI service's system database; only the platform role <c>AiBudgetAdmin</c> in the system tenant
///     changes it, and the AI service enforces that for every caller.
/// </summary>
[McpServerToolType]
public sealed class AiSpendTools
{
    /// <summary>Read cap and spend.</summary>
    [McpServerTool(Name = "get_ai_spend")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Read the monthly LLM spend cap (EUR), the key source and the spend of the current and previous UTC month " +
        "(spent EUR, reserved EUR, call count, reset time) of the central-key LLM gateway. Without targetTenantId it " +
        "reads the resolved tenant itself (read-only, any user with read access). With targetTenantId it reads another " +
        "tenant through the cap administration, which needs a session of the system tenant with the role AiBudgetAdmin. " +
        "A tenant without a cap reads as key source None. Equivalent to octo-cli GetAiSpend.")]
    public static async Task<AiSpendResponse> GetAiSpend(
        McpServer server,
        [Description("Tenant to read as platform admin (system-tenant session, role AiBudgetAdmin). Omit to read the " +
                     "resolved tenant itself.")]
        string? targetTenantId = null,
        [Description("Tenant of the session. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AiClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AiSpendResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var report = string.IsNullOrWhiteSpace(targetTenantId)
                ? await ctx.Client!.GetAiSpendAsync()
                : await ctx.Client!.GetAiSpendCapAsync(targetTenantId.Trim());
            return new AiSpendResponse
            {
                IsSuccess = true,
                Spend = report,
                Message = $"Tenant '{report.TenantId}': key source {report.KeySource}, cap EUR {report.CapMonthlyEur}."
            };
        }
        catch (Exception ex)
        {
            return new AiSpendResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Set the cap of a tenant. Platform role only; requires confirm and a reason.</summary>
    [McpServerTool(Name = "set_ai_spend_cap")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Set the monthly LLM spend cap in EUR (and the key source, default CentralKey) of a tenant for the central-key " +
        "LLM gateway. This decides how much Meshmakers' central LLM key may spend for the tenant per UTC month. Only a " +
        "session of the system tenant with the platform role AiBudgetAdmin succeeds; the AI service refuses everybody " +
        "else (403). Every change is audited with old and new value, who and the reason. Takes effect in the gateway " +
        "within at most 60 seconds. A cap of 0 stops the tenant's central-key calls. Requires confirm=true and a reason. " +
        "Equivalent to octo-cli SetAiSpendCap.")]
    public static async Task<AiSpendCapChangeResponse> SetAiSpendCap(
        McpServer server,
        [Description("Tenant whose cap is set.")] string targetTenantId,
        [Description("Monthly cap in EUR (0 or more).")] decimal capMonthlyEur,
        [Description("Why the cap changes (stored in the audit record, 1-500 characters).")] string reason,
        [Description("Must be true to actually change the cap.")] bool confirm = false,
        [Description("Key source: CentralKey (default), None, ByoKey or Subscription.")] string keySource = "CentralKey",
        [Description("Tenant of the session (the system tenant). Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new AiSpendCapChangeResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to change the LLM spend cap without confirm=true."
            };
        }

        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            return new AiSpendCapChangeResponse { IsSuccess = false, ErrorMessage = "targetTenantId is required." };
        }

        if (capMonthlyEur < 0)
        {
            return new AiSpendCapChangeResponse { IsSuccess = false, ErrorMessage = "capMonthlyEur must be 0 or more." };
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            return new AiSpendCapChangeResponse
            {
                IsSuccess = false,
                ErrorMessage = "A reason of 1 to 500 characters is required for the audit record."
            };
        }

        var ctx = await AiClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AiSpendCapChangeResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var change = await ctx.Client!.SetAiSpendCapAsync(targetTenantId.Trim(), new SetAiSpendCapRequestDto
            {
                CapMonthlyEur = capMonthlyEur,
                KeySource = keySource,
                Reason = reason.Trim()
            });
            return new AiSpendCapChangeResponse
            {
                IsSuccess = true,
                Change = change,
                Message = $"LLM spend cap of tenant '{change.TenantId}': {change.OldKeySource} EUR " +
                          $"{change.OldCapMonthlyEur} -> {change.NewKeySource} EUR {change.NewCapMonthlyEur}."
            };
        }
        catch (Exception ex)
        {
            return new AiSpendCapChangeResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
