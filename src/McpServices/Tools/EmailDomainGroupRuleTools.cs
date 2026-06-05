using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Email-domain group rules. Mirrors octo-cli EmailDomainGroupRules commands.</summary>
[McpServerToolType]
public sealed class EmailDomainGroupRuleTools
{
    /// <summary>List all rules.</summary>
    [McpServerTool(Name = "get_email_domain_group_rules")]
    [Description("List all email-domain group rules. Equivalent to octo-cli GetEmailDomainGroupRules.")]
    public static async Task<GetEmailDomainGroupRulesResponse> GetRules(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetEmailDomainGroupRulesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var rules = (await ctx.Client!.GetEmailDomainGroupRules()).ToList();
            return new GetEmailDomainGroupRulesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Rules = rules,
                TotalCount = rules.Count
            };
        }
        catch (Exception ex)
        {
            return new GetEmailDomainGroupRulesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get a single rule by runtime id.</summary>
    [McpServerTool(Name = "get_email_domain_group_rule")]
    [Description("Get a single email-domain group rule by runtime id. Equivalent to octo-cli GetEmailDomainGroupRule.")]
    public static async Task<EmailDomainGroupRuleResponse> GetRule(
        McpServer server,
        [Description("Runtime id of the rule.")] string rtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var rule = await ctx.Client!.GetEmailDomainGroupRule(new OctoObjectId(rtId));
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Rule = rule
            };
        }
        catch (Exception ex)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new rule.</summary>
    [McpServerTool(Name = "create_email_domain_group_rule")]
    [Description(
        "Create a new email-domain group rule. New users whose email matches the pattern are auto-added to the " +
        "target group. Equivalent to octo-cli CreateEmailDomainGroupRule.")]
    public static async Task<EmailDomainGroupRuleResponse> CreateRule(
        McpServer server,
        [Description("Email-domain pattern (e.g. 'meshmakers.io').")] string emailDomainPattern,
        [Description("Runtime id of the target group.")] string targetGroupRtId,
        [Description("Optional description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(emailDomainPattern) || string.IsNullOrWhiteSpace(targetGroupRtId))
        {
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = false,
                ErrorMessage = "emailDomainPattern and targetGroupRtId are required."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateEmailDomainGroupRule(new EmailDomainGroupRuleDto
            {
                EmailDomainPattern = emailDomainPattern,
                TargetGroupRtId = targetGroupRtId,
                Description = description
            });
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Email-domain group rule for '{emailDomainPattern}' created."
            };
        }
        catch (Exception ex)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update an existing rule.</summary>
    [McpServerTool(Name = "update_email_domain_group_rule")]
    [Description("Update an email-domain group rule. Equivalent to octo-cli UpdateEmailDomainGroupRule.")]
    public static async Task<EmailDomainGroupRuleResponse> UpdateRule(
        McpServer server,
        [Description("Runtime id of the rule.")] string rtId,
        [Description("Email-domain pattern.")] string emailDomainPattern,
        [Description("Runtime id of the target group.")] string targetGroupRtId,
        [Description("Optional description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId) || string.IsNullOrWhiteSpace(emailDomainPattern) ||
            string.IsNullOrWhiteSpace(targetGroupRtId))
        {
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = false,
                ErrorMessage = "rtId, emailDomainPattern and targetGroupRtId are required."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateEmailDomainGroupRule(new OctoObjectId(rtId), new EmailDomainGroupRuleDto
            {
                EmailDomainPattern = emailDomainPattern,
                TargetGroupRtId = targetGroupRtId,
                Description = description
            });
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Message = $"Email-domain group rule '{rtId}' updated."
            };
        }
        catch (Exception ex)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a rule. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_email_domain_group_rule")]
    [Description(
        "Delete an email-domain group rule. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteEmailDomainGroupRule.")]
    public static async Task<EmailDomainGroupRuleResponse> DeleteRule(
        McpServer server,
        [Description("Runtime id of the rule.")] string rtId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        if (!confirm)
        {
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete email-domain group rule '{rtId}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteEmailDomainGroupRule(new OctoObjectId(rtId));
            return new EmailDomainGroupRuleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Message = $"Email-domain group rule '{rtId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new EmailDomainGroupRuleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
