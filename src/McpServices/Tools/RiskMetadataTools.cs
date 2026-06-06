using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tools that expose tool-level risk classification to MCP clients. Used by the AI Adapter
///     worker at session start to populate its PreToolUse-hook decision table.
/// </summary>
[McpServerToolType]
public sealed class RiskMetadataTools
{
    /// <summary>
    ///     Returns the risk classification of every registered MCP tool. The AI Adapter worker
    ///     calls this once at session start and caches the result; the PreToolUse hook then
    ///     decides locally whether each tool call needs to be gated through the approval flow.
    /// </summary>
    /// <param name="server">MCP server instance.</param>
    /// <returns>The complete risk map plus the non-default subset.</returns>
    [McpServerTool(Name = "get_tool_risk_metadata")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Return the risk classification (Low / Medium / High) of every registered MCP tool. " +
        "AI clients should call this once at session start to populate their pre-tool-use " +
        "decision table — the classification drives the user-facing approval gate, not " +
        "authorisation. Tools without an explicit classification default to Low.")]
    public static ToolRiskMetadataResponse GetToolRiskMetadata(McpServer server)
    {
        try
        {
            var registry = server.Services!.GetRequiredService<IToolRiskRegistry>();
            return new ToolRiskMetadataResponse
            {
                IsSuccess = true,
                RiskByTool = registry.GetAll(),
                NonDefaultRiskByTool = registry.GetAllNonDefault()
            };
        }
        catch (Exception ex)
        {
            return new ToolRiskMetadataResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
