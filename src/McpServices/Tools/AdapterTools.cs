using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Adapter inventory and schema discovery tools (Communication Controller). Mirrors octo-cli Adapter
///     commands.
/// </summary>
[McpServerToolType]
public sealed class AdapterTools
{
    /// <summary>List all adapters for the tenant.</summary>
    [McpServerTool(Name = "get_adapters")]
    [Description("List all adapters configured for the tenant. Equivalent to octo-cli GetAdapters.")]
    public static async Task<GetAdaptersResponse> GetAdapters(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetAdaptersResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var adapters = (await ctx.Client!.GetAdaptersAsync()).ToList();
            return new GetAdaptersResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Adapters = adapters,
                TotalCount = adapters.Count,
                Message = adapters.Count == 0 ? "No adapters configured." : $"{adapters.Count} adapter(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetAdaptersResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the full configuration of one adapter.</summary>
    [McpServerTool(Name = "get_adapter")]
    [Description("Get the full configuration of a single adapter. Equivalent to octo-cli GetAdapter.")]
    public static async Task<GetAdapterResponse> GetAdapter(
        McpServer server,
        [Description("Adapter runtime ID.")] string adapterId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return new GetAdapterResponse { IsSuccess = false, ErrorMessage = "adapterId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetAdapterResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var adapter = await ctx.Client!.GetAdapterConfigurationAsync(adapterId);
            return new GetAdapterResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                Adapter = adapter
            };
        }
        catch (Exception ex)
        {
            return new GetAdapterResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the aggregated node descriptors from all connected adapters.</summary>
    [McpServerTool(Name = "get_adapter_nodes")]
    [Description(
        "Return aggregated pipeline-node descriptors from all currently-connected adapters as JSON. " +
        "Equivalent to octo-cli GetAdapterNodes.")]
    public static async Task<GetAdapterNodesResponse> GetAdapterNodes(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetAdapterNodesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var nodes = await ctx.Client!.GetAdapterNodesAsync();
            return new GetAdapterNodesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                NodesJson = nodes
            };
        }
        catch (Exception ex)
        {
            return new GetAdapterNodesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the composite pipeline JSON Schema for one adapter.</summary>
    [McpServerTool(Name = "get_pipeline_schema")]
    [Description(
        "Return the composite pipeline JSON Schema for the given adapter — describes the union of node types " +
        "that pipelines on this adapter may use. Equivalent to octo-cli GetPipelineSchema.")]
    public static async Task<GetPipelineSchemaResponse> GetPipelineSchema(
        McpServer server,
        [Description("Adapter runtime ID.")] string adapterId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return new GetPipelineSchemaResponse { IsSuccess = false, ErrorMessage = "adapterId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetPipelineSchemaResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var schema = await ctx.Client!.GetPipelineSchemaAsync(adapterId);
            return new GetPipelineSchemaResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                SchemaJson = schema
            };
        }
        catch (Exception ex)
        {
            return new GetPipelineSchemaResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
