using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     The ready state of a starter tenant (AB#9573), checked by the step <c>verify_ready</c> of
///     <c>create_tenant starter=true</c>: what System.Ai.Starter 1.1 brings through System.Ai.Builder 1.2 is there and
///     usable for the tenant's first user - the builder Agent enabled, its Mandate with write mode Propose, its
///     service-account client, and the knowledge page <c>app-index</c>. It reads with the caller's own rights in the
///     tenant (the same read path as <c>knowledge_search</c>), so "ready" also means the first user can see them.
/// </summary>
internal static class StarterReadyCheck
{
    internal const string AgentType = "System.Ai/Agent";
    internal const string MandateType = "System.Ai/Mandate";
    internal const string ClientType = "System.Identity/Client";

    /// <summary>Well-known names of the System.Ai.Builder 1.2 seed (AB#9004).</summary>
    internal const string AgentName = "builder-agent";

    internal const string MandateName = "builder-mandate";
    internal const string ClientName = "builder-agent-client";

    /// <summary>The knowledge page a first user is sent to for "build an app".</summary>
    internal const string AppIndexSlug = "app-index";

    /// <summary><c>System.Ai/WriteMode</c> value Propose.</summary>
    internal const int WriteModePropose = 0;

    /// <summary>
    ///     Opens the tenant with the caller's rights and returns what is missing, or null when the tenant is ready.
    /// </summary>
    internal static async Task<string?> MissingAsync(McpServer server, string tenantId)
    {
        var ctx = await KnowledgeTools.OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return ctx.Error;
        }

        using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
        var missing = await MissingAsync(ctx.Repo, session);
        return missing.Count == 0 ? null : string.Join("; ", missing);
    }

    /// <summary>Every part of the ready state that is missing, in a fixed order; empty when the tenant is ready.</summary>
    internal static async Task<IReadOnlyList<string>> MissingAsync(ITenantRepository repository, IOctoSession session)
    {
        var missing = new List<string>();

        var agent = await KnowledgePages.FindBySlugAsync(repository, session, AgentType, AgentName);
        if (agent == null)
        {
            missing.Add($"no {AgentType} '{AgentName}' (System.Ai.Builder 1.2 or later not installed?)");
        }
        else if (!IsTrue(agent.GetAttributeValueOrDefault("Enabled")))
        {
            missing.Add($"{AgentType} '{AgentName}' is not enabled");
        }

        var mandate = await KnowledgePages.FindBySlugAsync(repository, session, MandateType, MandateName);
        if (mandate == null)
        {
            missing.Add($"no {MandateType} '{MandateName}'");
        }
        else if (EnumValue(mandate.GetAttributeValueOrDefault("WriteMode")) != WriteModePropose)
        {
            missing.Add($"{MandateType} '{MandateName}' does not have write mode Propose");
        }

        if (await KnowledgePages.FindBySlugAsync(repository, session, ClientType, ClientName) == null)
        {
            missing.Add($"no {ClientType} '{ClientName}' (the builder agent's service account)");
        }

        if (await KnowledgePages.FindBySlugAsync(repository, session, KnowledgePages.PageType, AppIndexSlug) == null)
        {
            missing.Add($"no knowledge page '{AppIndexSlug}'");
        }

        return missing;
    }

    /// <summary>An enum attribute as its number; null when it is missing or not a number (never read as Propose).</summary>
    private static int? EnumValue(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture),
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
        _ => null
    };

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) && parsed,
        _ => false
    };
}
