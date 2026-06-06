using Meshmakers.Octo.Backend.McpServices.Models;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Read-only registry of MCP tool risk classifications. Built once at service startup by
///     reflecting over every <c>[McpServerTool]</c>-attributed method and recording the value of an
///     optional <see cref="McpRiskAttribute" /> on the same method.
/// </summary>
public interface IToolRiskRegistry
{
    /// <summary>
    ///     Returns the classification for the named tool. Tools without an explicit
    ///     <see cref="McpRiskAttribute" /> default to <see cref="McpRiskLevel.Low" />, matching the
    ///     convention that read-heavy / narrow-scope operations are the default.
    /// </summary>
    /// <param name="toolName">The snake_case tool name as exposed to the MCP client.</param>
    /// <returns>The risk classification, or <see cref="McpRiskLevel.Low" /> when none is declared.</returns>
    McpRiskLevel GetRiskLevel(string toolName);

    /// <summary>
    ///     Returns every tool that has an explicit (non-default) risk classification. The AI Adapter
    ///     worker calls this once at session start and uses the result to drive its PreToolUse gate.
    /// </summary>
    IReadOnlyDictionary<string, McpRiskLevel> GetAllNonDefault();

    /// <summary>
    ///     Returns the complete map of tool name to classification, including the default
    ///     <see cref="McpRiskLevel.Low" /> entries. Useful for diagnostic introspection.
    /// </summary>
    IReadOnlyDictionary<string, McpRiskLevel> GetAll();
}
