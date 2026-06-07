using System.ComponentModel;
using System.Globalization;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Resources;

/// <summary>
///     MCP Resources for materialising a tenant's Construction Kit schema as Markdown.
///     The AI Adapter worker reads these at session start so Claude sees the CK type
///     graph it can address — without round-tripping through Tools repeatedly (ADR-25 §5d).
/// </summary>
[McpServerResourceType]
// ReSharper disable once UnusedType.Global
public sealed class CkSchemaResources
{
    /// <summary>
    ///     System CK models (System, System.Bot, System.Communication, System.Ai) as Markdown.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the tenant + cache services.</param>
    /// <param name="tenantId">Tenant id parsed from the resource URI.</param>
    /// <returns>Markdown rendering of every System.* model's types, attributes and associations.</returns>
    [McpServerResource(
        UriTemplate = "ck-schema://{tenantId}/system",
        Name = "ck-schema-system",
        Title = "System CK Schema",
        MimeType = "text/markdown")]
    [Description(
        "Markdown export of the System CK models (System, System.Bot, System.Communication, System.Ai) " +
        "for the requested tenant. Materialised into the worker's CLAUDE.md at session start so the agent " +
        "knows what platform types it can read or mutate. URI: ck-schema://{tenantId}/system.")]
    public static async Task<string> GetSystemSchemaAsync(
        McpServer server,
        string tenantId)
    {
        return await RenderSchemaAsync(server, tenantId, scope: SchemaScope.System);
    }

    /// <summary>
    ///     Tenant-specific (non-System) CK models as Markdown. Returns empty if the tenant only has the
    ///     System base models loaded.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the tenant + cache services.</param>
    /// <param name="tenantId">Tenant id parsed from the resource URI.</param>
    /// <returns>Markdown rendering of every non-System model's types, attributes and associations.</returns>
    [McpServerResource(
        UriTemplate = "ck-schema://{tenantId}/domain",
        Name = "ck-schema-domain",
        Title = "Domain CK Schema",
        MimeType = "text/markdown")]
    [Description(
        "Markdown export of the tenant-specific (non-System) CK models — the domain types installed on " +
        "this tenant via blueprints. Reflects the same CK cache the Studio paints from. " +
        "URI: ck-schema://{tenantId}/domain.")]
    public static async Task<string> GetDomainSchemaAsync(
        McpServer server,
        string tenantId)
    {
        return await RenderSchemaAsync(server, tenantId, scope: SchemaScope.Domain);
    }

    private static async Task<string> RenderSchemaAsync(McpServer server, string tenantId, SchemaScope scope)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return "# CK Schema\n\n_Error: tenantId is required._\n";
        }

        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var resolvedTenant = tenantRepository.TenantId;
            var modelIds = ckCacheService.GetCkModelIds(resolvedTenant);
            var filteredModels = modelIds
                .Where(m => MatchesScope(m.FullName, scope))
                .OrderBy(m => m.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var builder = new StringBuilder();
            builder.Append("# CK Schema — ").Append(scope == SchemaScope.System ? "System" : "Domain").Append('\n');
            builder.Append("\nTenant: `").Append(resolvedTenant).Append("`  \n");
            builder.Append("Models: ").Append(filteredModels.Count).Append("\n\n");

            if (filteredModels.Count == 0)
            {
                builder.Append("_No ").Append(scope == SchemaScope.System ? "System" : "domain")
                    .Append(" models loaded for this tenant._\n");
                return builder.ToString();
            }

            var allTypes = ckCacheService.GetCkTypes(resolvedTenant).ToList();

            foreach (var modelId in filteredModels)
            {
                AppendModel(builder, modelId.ToString(CultureInfo.InvariantCulture), allTypes);
            }

            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"# CK Schema\n\n_Error: {ex.Message}_\n";
        }
    }

    private static void AppendModel(StringBuilder builder, string modelFullName, IReadOnlyCollection<CkTypeGraph> allTypes)
    {
        builder.Append("## ").Append(modelFullName).Append("\n\n");

        var modelTypes = allTypes
            .Where(t => t.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture) == modelFullName)
            .OrderBy(t => t.CkTypeId.ElementId.SemanticVersionedFullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modelTypes.Count == 0)
        {
            builder.Append("_No types._\n\n");
            return;
        }

        foreach (var typeGraph in modelTypes)
        {
            AppendType(builder, typeGraph);
        }
    }

    private static void AppendType(StringBuilder builder, CkTypeGraph typeGraph)
    {
        builder.Append("### ").Append(typeGraph.CkTypeId.ElementId.SemanticVersionedFullName);
        if (typeGraph.IsAbstract)
        {
            builder.Append(" *(abstract)*");
        }
        builder.Append("\n\n");

        if (!string.IsNullOrWhiteSpace(typeGraph.Description))
        {
            builder.Append(typeGraph.Description).Append("\n\n");
        }

        if (typeGraph.DerivedFromCkTypeId != null)
        {
            builder.Append("Base: `").Append(typeGraph.DerivedFromCkTypeId).Append("`  \n");
        }

        var attributes = typeGraph.AllAttributes.OrderBy(a => a.Key.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        if (attributes.Count > 0)
        {
            builder.Append("\n**Attributes**\n\n");
            foreach (var attribute in attributes)
            {
                builder.Append("- `").Append(attribute.Value.AttributeName).Append("` — ")
                    .Append(attribute.Value.ValueType);
                if (attribute.Value.IsOptional)
                {
                    builder.Append(" *(optional)*");
                }
                builder.Append('\n');
            }
        }

        var outboundAssociations = typeGraph.Associations.Out.All.ToList();
        if (outboundAssociations.Count > 0)
        {
            builder.Append("\n**Outbound associations**\n\n");
            foreach (var assoc in outboundAssociations)
            {
                builder.Append("- `").Append(assoc.CkRoleId.FullName).Append("` → `")
                    .Append(assoc.TargetCkTypeId.FullName).Append("` (")
                    .Append(assoc.Multiplicity).Append(")\n");
            }
        }

        builder.Append('\n');
    }

    private static bool MatchesScope(string modelFullName, SchemaScope scope)
    {
        // The CK model id format is "Name-Version" (e.g. "System-1", "System.Ai-3", "EnergyCommunity-1").
        // System-rooted models all start with "System" followed by either '-' (root model) or '.' (a sub-model).
        var isSystem = modelFullName.StartsWith("System-", StringComparison.OrdinalIgnoreCase)
                       || modelFullName.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
        return scope == SchemaScope.System ? isSystem : !isSystem;
    }

    private enum SchemaScope
    {
        System,
        Domain
    }
}
