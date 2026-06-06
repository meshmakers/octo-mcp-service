using System.Reflection;
using Meshmakers.Octo.Backend.McpServices.Models;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Default <see cref="IToolRiskRegistry" /> implementation. Scans the assembly that hosts this
///     service for every <c>[McpServerTool]</c>-attributed method and records its
///     <see cref="McpRiskAttribute" /> classification (defaulting to <see cref="McpRiskLevel.Low" />).
///     Registered as a singleton — the scan is done once during construction.
/// </summary>
public sealed class ToolRiskRegistry : IToolRiskRegistry
{
    private readonly IReadOnlyDictionary<string, McpRiskLevel> _allClassifications;
    private readonly IReadOnlyDictionary<string, McpRiskLevel> _nonDefaultClassifications;

    /// <summary>
    ///     Initialises the registry by reflecting over the calling assembly.
    /// </summary>
    public ToolRiskRegistry() : this(typeof(ToolRiskRegistry).Assembly)
    {
    }

    /// <summary>
    ///     Initialises the registry by reflecting over the given assembly. Test seam.
    /// </summary>
    /// <param name="assembly">The assembly to scan for tool methods.</param>
    public ToolRiskRegistry(Assembly assembly)
    {
        var all = new Dictionary<string, McpRiskLevel>(StringComparer.Ordinal);
        var nonDefault = new Dictionary<string, McpRiskLevel>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in type.GetMethods(flags))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr?.Name is null)
                {
                    continue;
                }

                var riskAttr = method.GetCustomAttribute<McpRiskAttribute>();
                var level = riskAttr?.Level ?? McpRiskLevel.Low;

                all[toolAttr.Name] = level;
                if (riskAttr is not null)
                {
                    nonDefault[toolAttr.Name] = level;
                }
            }
        }

        _allClassifications = all;
        _nonDefaultClassifications = nonDefault;
    }

    /// <inheritdoc />
    public McpRiskLevel GetRiskLevel(string toolName)
    {
        return _allClassifications.TryGetValue(toolName, out var level) ? level : McpRiskLevel.Low;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, McpRiskLevel> GetAllNonDefault()
    {
        return _nonDefaultClassifications;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, McpRiskLevel> GetAll()
    {
        return _allClassifications;
    }
}
