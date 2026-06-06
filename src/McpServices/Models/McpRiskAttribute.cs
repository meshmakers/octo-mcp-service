namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Declares the risk classification of an MCP tool method. Read at startup by
///     <see cref="Services.IToolRiskRegistry" /> and surfaced via the <c>get_tool_risk_metadata</c>
///     tool so the AI Adapter worker's PreToolUse hook can decide whether to gate the call.
/// </summary>
/// <remarks>
///     Apply on the same method that carries the <c>[McpServerTool]</c> attribute. Tools without
///     this attribute default to <see cref="McpRiskLevel.Low" />. The attribute is purely
///     informational on the MCP server side — no authorisation decision is made from it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpRiskAttribute : Attribute
{
    /// <summary>
    ///     Initialises a new instance with the given risk classification.
    /// </summary>
    /// <param name="level">The classification.</param>
    public McpRiskAttribute(McpRiskLevel level)
    {
        Level = level;
    }

    /// <summary>
    ///     The risk classification of the annotated tool.
    /// </summary>
    public McpRiskLevel Level { get; }
}
