using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Size limits <c>validate_pipeline_definition</c> checks before it parses or evaluates a caller-supplied
///     pipeline definition, so one call cannot exhaust the memory or the CPU of the shared MCP process. A definition
///     above a limit is refused with <see cref="RefusedCode" /> and a message that names the limit; it is never
///     evaluated. The limits are far above any real pipeline (the largest shipped pipelines have a few hundred
///     nodes and nest control nodes a handful of levels deep).
/// </summary>
internal static class PipelineDefinitionLimits
{
    /// <summary>Error code of a definition refused by one of the limits.</summary>
    public const string RefusedCode = "PIPELINE_DEFINITION_LIMIT_EXCEEDED";

    /// <summary>Maximum length of the definition text, in characters.</summary>
    public const int MaxDefinitionLength = 1_000_000;

    /// <summary>
    ///     Maximum number of YAML nodes (scalars, sequences, mappings) after expanding aliases. Aliases share a node
    ///     in the YAML document but are copied by the conversion to JSON, so a small document of nested aliases can
    ///     expand to billions of nodes.
    /// </summary>
    public const long MaxExpandedYamlNodes = 200_000;

    /// <summary>Maximum number of pipeline nodes (triggers, transformations and nested node lists).</summary>
    public const int MaxPipelineNodes = 2_000;

    /// <summary>
    ///     Maximum nesting depth of node lists: a root-level node has depth 1, a node in the <c>transformations</c>
    ///     of a root-level control node depth 2, and so on.
    /// </summary>
    public const int MaxNestingDepth = 32;

    /// <summary>
    ///     Checks the raw definition text: its length and, for YAML, the number of nodes after alias expansion.
    ///     Text that does not tokenize as YAML passes here; the parser reports it afterwards.
    /// </summary>
    /// <returns>The refusal message, or null when the text is within the limits.</returns>
    public static string? CheckText(string definition)
    {
        if (definition.Length > MaxDefinitionLength)
        {
            return $"The definition has {definition.Length} characters; the limit is {MaxDefinitionLength}. " +
                   "Split the pipeline or move large data out of the definition.";
        }

        long expanded;
        try
        {
            expanded = CountExpandedYamlNodes(definition, MaxExpandedYamlNodes);
        }
        catch (YamlException)
        {
            return null;
        }

        return expanded > MaxExpandedYamlNodes
            ? $"The definition expands to more than {MaxExpandedYamlNodes} YAML nodes once its aliases are " +
              "resolved; the limit is " + MaxExpandedYamlNodes + ". Write the repeated parts out instead of " +
              "nesting aliases."
            : null;
    }

    /// <summary>
    ///     Checks the parsed definition: the number of pipeline nodes and the nesting depth of node lists.
    /// </summary>
    /// <returns>The refusal message, or null when the definition is within the limits.</returns>
    public static string? CheckNodes(JsonNode definition)
    {
        var (count, depth) = MeasureNodes(definition, MaxPipelineNodes, MaxNestingDepth);
        if (depth > MaxNestingDepth)
        {
            return $"The definition nests node lists more than {MaxNestingDepth} levels deep (transformations " +
                   $"inside control nodes such as If, ForEach or Switch); the limit is {MaxNestingDepth}.";
        }

        return count > MaxPipelineNodes
            ? $"The definition has more than {MaxPipelineNodes} nodes; the limit is {MaxPipelineNodes}. Split the " +
              "pipeline into several pipelines of a data flow."
            : null;
    }

    /// <summary>
    ///     Counts the YAML nodes of <paramref name="text" /> as the conversion to JSON would produce them, with
    ///     every alias expanded to the size of its anchored node. Stops counting once <paramref name="stopAbove" />
    ///     is exceeded and returns the count reached so far.
    /// </summary>
    internal static long CountExpandedYamlNodes(string text, long stopAbove)
    {
        var parser = new Parser(new StringReader(text));
        var anchorSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var open = new Stack<(string? Anchor, long Size)>();
        long total = 0;

        void Add(long size)
        {
            total += size;
            if (open.Count > 0)
            {
                var (anchor, current) = open.Pop();
                open.Push((anchor, current + size));
            }
        }

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case Scalar scalar:
                    Add(1);
                    if (!scalar.Anchor.IsEmpty)
                    {
                        anchorSizes[scalar.Anchor.Value] = 1;
                    }

                    break;
                case SequenceStart sequence:
                    total++;
                    open.Push((sequence.Anchor.IsEmpty ? null : sequence.Anchor.Value, 1));
                    break;
                case MappingStart mapping:
                    total++;
                    open.Push((mapping.Anchor.IsEmpty ? null : mapping.Anchor.Value, 1));
                    break;
                case SequenceEnd or MappingEnd when open.Count > 0:
                {
                    var (anchor, size) = open.Pop();
                    if (anchor != null)
                    {
                        anchorSizes[anchor] = size;
                    }

                    if (open.Count > 0)
                    {
                        var (parentAnchor, parentSize) = open.Pop();
                        open.Push((parentAnchor, parentSize + size));
                    }

                    break;
                }
                case AnchorAlias alias:
                    // An alias to an anchor that is still open (a recursive anchor) or unknown counts as one node;
                    // the parser or the converter refuses those documents afterwards.
                    Add(anchorSizes.GetValueOrDefault(alias.Value.Value, 1));
                    break;
            }

            if (total > stopAbove)
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>
    ///     Counts the pipeline nodes and measures the nesting depth of node lists, following the node containers of
    ///     the pipeline SDK like <c>CountPipelineNodes</c>: root <c>triggers</c>, <c>transformations</c> and
    ///     <c>nodes</c>; inside a node its <c>transformations</c>, the <c>cases[].transformations</c> and the
    ///     <c>default</c> list of a Switch. Stops once either limit is exceeded.
    /// </summary>
    internal static (int Count, int Depth) MeasureNodes(JsonNode? definition, int maxCount, int maxDepth)
    {
        if (definition is not JsonObject root)
        {
            return (0, 0);
        }

        var count = 0;
        var deepest = 0;
        var pending = new Stack<(JsonNode? List, int Depth)>();
        pending.Push((Property(root, "nodes"), 1));
        pending.Push((Property(root, "transformations"), 1));
        pending.Push((Property(root, "triggers"), 1));
        while (pending.Count > 0)
        {
            var (list, depth) = pending.Pop();
            if (list is not JsonArray array)
            {
                continue;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                count++;
                deepest = Math.Max(deepest, depth);
                if (count > maxCount || deepest > maxDepth)
                {
                    return (count, deepest);
                }

                pending.Push((Property(node, "transformations"), depth + 1));
                if (Property(node, "cases") is JsonArray cases)
                {
                    foreach (var switchCase in cases.OfType<JsonObject>())
                    {
                        pending.Push((Property(switchCase, "transformations"), depth + 1));
                    }

                    pending.Push((Property(node, "default"), depth + 1));
                }
            }
        }

        return (count, deepest);
    }

    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
