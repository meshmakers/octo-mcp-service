using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Tools;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Resources;

/// <summary>
///     Knowledge pages as an MCP resource template (AB#8484): a thin alias of <c>knowledge_get</c> with the same
///     tenant gate, caller session and rendering. The tools stay the primary path (finding of AB#8483, octo-ai-services
///     <c>docs/concepts/knowledge-base-mcp-context.md</c>); <c>knowledge://</c> is taken by <c>AiKnowledgeSource</c>.
/// </summary>
[McpServerResourceType]
// ReSharper disable once UnusedType.Global
public sealed class KnowledgePageResources
{
    /// <summary>Renders the current version of a knowledge page as markdown.</summary>
    /// <param name="server">MCP server instance.</param>
    /// <param name="tenantId">Tenant id from the URI.</param>
    /// <param name="slug">Slug of the page.</param>
    /// <returns>The page as markdown, or an error text.</returns>
    [McpServerResource(
        UriTemplate = "knowledge-page://{tenantId}/{slug}",
        Name = "knowledge-page",
        Title = "Knowledge page",
        MimeType = "text/markdown")]
    [Description(
        "A page of the tenant's knowledge base as markdown (current version). Same content as the knowledge_get tool. " +
        "URI: knowledge-page://{tenantId}/{slug}; the slugs come from knowledge_search.")]
    public static async Task<string> GetKnowledgePageAsync(McpServer server, string tenantId, string slug)
    {
        var result = await KnowledgeTools.KnowledgeGet(server, slug, null, tenantId);
        return result.IsSuccess ? result.Markdown! : $"# Knowledge page\n\n_Error: {result.ErrorMessage}_\n";
    }
}
