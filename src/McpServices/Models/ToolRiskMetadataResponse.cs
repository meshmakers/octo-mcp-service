namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response envelope for <c>get_tool_risk_metadata</c>. Returns the complete risk map so the
///     AI Adapter worker can populate its PreToolUse-hook decision table once at session start
///     instead of round-tripping the MCP server for every tool call.
/// </summary>
public class ToolRiskMetadataResponse
{
    /// <summary>
    ///     Whether the call succeeded.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    ///     Error message when <see cref="IsSuccess" /> is <c>false</c>.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Map of tool name to risk level, covering every registered tool. Tools without an
    ///     explicit <c>[McpRisk]</c> declaration appear here with value <see cref="McpRiskLevel.Low" />.
    /// </summary>
    public IReadOnlyDictionary<string, McpRiskLevel> RiskByTool { get; set; } =
        new Dictionary<string, McpRiskLevel>();

    /// <summary>
    ///     Subset of <see cref="RiskByTool" /> that carries an explicit non-default classification.
    ///     Cheaper for the worker to consume when it only cares about the gated tools.
    /// </summary>
    public IReadOnlyDictionary<string, McpRiskLevel> NonDefaultRiskByTool { get; set; } =
        new Dictionary<string, McpRiskLevel>();
}
