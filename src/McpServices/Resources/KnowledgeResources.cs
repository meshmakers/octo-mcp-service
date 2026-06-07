using System.ComponentModel;
using System.Globalization;
using System.Text;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Resources;

/// <summary>
///     MCP Resources that materialise tenant-specific AI knowledge sources for the worker at session
///     start. One resource per <c>AiKnowledgeSource</c> entity, addressed by its runtime id; content is
///     dispatched on the knowledge kind enum (ClaudeMd / Url / McpResource / RagDoc).
/// </summary>
[McpServerResourceType]
// ReSharper disable once UnusedType.Global
public sealed class KnowledgeResources
{
    /// <summary>
    ///     CK type of an <c>AiKnowledgeSource</c> entity. The CK model id is "System.Ai-3" and the
    ///     element id is "AiKnowledgeSource-1" — version 1 of the type. If the System.Ai model is bumped
    ///     to AiKnowledgeSource-2 with a breaking attribute change, this constant must move with it
    ///     (the type's version is part of the runtime entity primary key).
    /// </summary>
    private const string AiKnowledgeSourceCkTypeId = "System.Ai-3/AiKnowledgeSource-1";

    /// <summary>
    ///     Returns the content of one <c>AiKnowledgeSource</c> instance as markdown.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to repositories and the HTTP client.</param>
    /// <param name="tenantId">Tenant id parsed from the resource URI.</param>
    /// <param name="rtId">Runtime id of the <c>AiKnowledgeSource</c> entity.</param>
    /// <param name="cancellationToken">Token surfaced from the MCP transport — cancels the outbound HTTP fetch on Url-kind sources.</param>
    /// <returns>Markdown with the entity metadata plus the dispatched content payload.</returns>
    [McpServerResource(
        UriTemplate = "knowledge://{tenantId}/{rtId}",
        Name = "knowledge-source",
        Title = "AI Knowledge Source",
        MimeType = "text/markdown")]
    [Description(
        "Materialises one tenant AiKnowledgeSource for the worker. ClaudeMd kind returns the entity's " +
        "Path as the file the worker should read from its workspace; Url kind fetches the absolute URL " +
        "and returns its body; McpResource kind returns the inner MCP URI for the worker to follow; " +
        "RagDoc is reserved for phase 2. URI: knowledge://{tenantId}/{rtId}.")]
    public static async Task<string> GetKnowledgeSourceAsync(
        McpServer server,
        string tenantId,
        string rtId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return RenderError("rtId is required.");
        }

        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
            var dtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
            var httpClientFactory = server.Services!.GetRequiredService<IHttpClientFactory>();
            var logger = server.Services!.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeResources>();

            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            if (!OctoObjectId.TryParse(rtId, out var objectId))
            {
                return RenderError($"Invalid rtId '{rtId}' — expected a 24-character hex ObjectId.");
            }

            var entityKey = new RtEntityId(new RtCkId<CkTypeId>(AiKnowledgeSourceCkTypeId), objectId);

            using var session = await tenantRepository.GetSessionAsync();
            var entity = await tenantRepository.GetRtEntityByRtIdAsync(session, entityKey);
            if (entity == null)
            {
                return RenderError($"AiKnowledgeSource '{rtId}' not found on tenant '{tenantRepository.TenantId}'.");
            }

            var dto = dtoMapper.ConvertToDto(tenantRepository.TenantId, entity,
                AttributeValueResolveFlags.ResolveEnumsToNames);

            var title = ReadStringAttribute(dto, "Title");
            var kindRaw = ReadStringAttribute(dto, "Kind");
            var path = ReadStringAttribute(dto, "Path");
            var appliesToScopes = ReadStringAttribute(dto, "AppliesToScopes");

            return await RenderKnowledgeAsync(
                tenantRepository.TenantId, rtId, title, kindRaw, path, appliesToScopes,
                httpClientFactory, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            return RenderError(ex.Message);
        }
    }

    private static async Task<string> RenderKnowledgeAsync(
        string tenantId, string rtId,
        string? title, string? kindRaw, string? path, string? appliesToScopes,
        IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "AI Knowledge Source" : title).Append("\n\n");
        builder.Append("- Tenant: `").Append(tenantId).Append("`\n");
        builder.Append("- rtId: `").Append(rtId).Append("`\n");
        builder.Append("- Kind: ").Append(string.IsNullOrWhiteSpace(kindRaw) ? "_unknown_" : kindRaw).Append('\n');
        builder.Append("- AppliesToScopes: `").Append(string.IsNullOrWhiteSpace(appliesToScopes) ? "*" : appliesToScopes).Append("`\n\n");

        switch (kindRaw)
        {
            case "ClaudeMd":
                builder.Append("## Content (ClaudeMd)\n\n");
                if (string.IsNullOrWhiteSpace(path))
                {
                    builder.Append("_No Path set — worker has nothing to materialise._\n");
                }
                else
                {
                    // The Path is workspace-relative; the worker reads the file from its own filesystem.
                    // We surface it as metadata so the worker knows what to inline into CLAUDE.md.
                    builder.Append("Workspace-relative path: `").Append(path).Append("`\n\n");
                    builder.Append("_The worker reads this file from its own workspace and inlines it into CLAUDE.md._\n");
                }
                break;

            case "Url":
                builder.Append("## Content (Url)\n\n");
                if (string.IsNullOrWhiteSpace(path))
                {
                    builder.Append("_No URL set._\n");
                }
                else
                {
                    builder.Append("Source: <").Append(path).Append(">\n\n");
                    await AppendFetchedBodyAsync(builder, path, httpClientFactory, logger, ct);
                }
                break;

            case "McpResource":
                builder.Append("## Content (McpResource passthrough)\n\n");
                if (string.IsNullOrWhiteSpace(path))
                {
                    builder.Append("_No MCP URI set._\n");
                }
                else
                {
                    builder.Append("Inner MCP URI: `").Append(path).Append("`\n\n");
                    builder.Append("_The worker follows this URI via its own MCP client._\n");
                }
                break;

            case "RagDoc":
                builder.Append("## Content (RagDoc)\n\n");
                builder.Append("_RagDoc kind is reserved for Phase 2 — vector retrieval is not wired yet._\n");
                break;

            default:
                builder.Append("## Content\n\n");
                builder.Append("_Unrecognised Kind '").Append(kindRaw ?? "<null>").Append("' — knowledge source not materialised._\n");
                break;
        }

        return builder.ToString();
    }

    private static async Task AppendFetchedBodyAsync(
        StringBuilder builder, string url,
        IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken ct)
    {
        // Named client "knowledge-fetch" lets ops set timeout / proxy / header policy in
        // appsettings.json without recompiling. Falls back to the default client if not configured.
        var http = httpClientFactory.CreateClient("knowledge-fetch");
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                builder.Append("_Fetch failed: HTTP ")
                    .Append((int)response.StatusCode)
                    .Append(' ')
                    .Append(response.ReasonPhrase)
                    .Append("._\n");
                return;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            builder.Append("```\n").Append(body.TrimEnd()).Append("\n```\n");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Knowledge fetch failed for {Url}", url);
            builder.Append("_Fetch failed: ").Append(ex.Message).Append("._\n");
        }
    }

    private static string? ReadStringAttribute(RtTypeWithAttributesDto dto, string attributeName)
    {
        if (dto.Attributes == null)
        {
            return null;
        }

        var attr = dto.Attributes.FirstOrDefault(a =>
            string.Equals(a.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.AttributeName, attributeName.ToCamelCase(), StringComparison.OrdinalIgnoreCase));
        return attr?.Value?.ToString();
    }

    private static string RenderError(string message)
    {
        return "# AI Knowledge Source\n\n_Error: " + message + "_\n";
    }
}
