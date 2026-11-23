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

    /// <summary>
    ///     Maximum nesting depth of the YAML (or JSON) document, the depth the YAML-to-JSON converter accepts. The
    ///     YAML loader builds its node tree recursively before the converter checks the depth, so a deeper document
    ///     would overflow the stack (an uncatchable crash) instead of being refused.
    /// </summary>
    public const int MaxDocumentDepth = 256;

    /// <summary>Maximum number of pipeline nodes (triggers, transformations and nested node lists).</summary>
    public const int MaxPipelineNodes = 2_000;

    /// <summary>
    ///     Maximum nesting depth of node lists: a root-level node has depth 1, a node in the <c>transformations</c>
    ///     of a root-level control node depth 2, and so on.
    /// </summary>
    public const int MaxNestingDepth = 32;

    /// <summary>
    ///     Checks the raw definition text before it is parsed: its length, its nesting depth, the number of nodes
    ///     after alias expansion, and aliases whose anchor is not complete yet (a recursive anchor, or an alias
    ///     before its anchor). The scan reads the token stream iteratively, so it cannot itself overflow the stack.
    ///     JSON is scanned the same way (it is YAML). Text that stops tokenizing passes here once the part before
    ///     the error is within the limits; the parser reports the error afterwards and reads no further.
    /// </summary>
    /// <returns>The refusal message, or null when the text is within the limits.</returns>
    public static string? CheckText(string definition)
    {
        if (definition.Length > MaxDefinitionLength)
        {
            return $"The definition has {definition.Length} characters; the limit is {MaxDefinitionLength}. " +
                   "Split the pipeline or move large data out of the definition.";
        }

        return ScanYaml(definition, MaxExpandedYamlNodes, MaxDocumentDepth).Refusal;
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
    ///     Scans <paramref name="text" /> as a YAML token stream and counts its nodes (scalars, sequences, mappings) as
    ///     the conversion to JSON would produce them, with every alias expanded to the size of its anchored node.
    ///     Fails closed: stops with a refusal once the count exceeds <paramref name="maxNodes" />, the nesting exceeds
    ///     <paramref name="maxDepth" />, or an alias names an anchor that is not complete yet (still open, i.e.
    ///     recursive, or not defined before the alias): the loader would resolve both into a cycle or a forward
    ///     reference the converter expands without bound. Text that stops tokenizing ends the scan without a refusal.
    /// </summary>
    internal static (long Expanded, string? Refusal) ScanYaml(string text, long maxNodes, int maxDepth)
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

        try
        {
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
                    case NodeEvent { Anchor: var anchor } and (SequenceStart or MappingStart):
                        total++;
                        if (!anchor.IsEmpty)
                        {
                            // Redefining an anchor makes later aliases refer to the new node; until it is complete
                            // an alias to it is recursive.
                            anchorSizes.Remove(anchor.Value);
                        }

                        open.Push((anchor.IsEmpty ? null : anchor.Value, 1));
                        if (open.Count > maxDepth)
                        {
                            return (total, $"The definition nests more than {maxDepth} levels deep; the limit is " +
                                           $"{maxDepth}.");
                        }

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
                        if (!anchorSizes.TryGetValue(alias.Value.Value, out var aliasSize))
                        {
                            return (total, $"The alias *{alias.Value.Value} refers to an anchor that is not defined " +
                                           "before it or that contains the alias itself; recursive and forward " +
                                           "aliases are not accepted.");
                        }

                        Add(aliasSize);
                        break;
                }

                if (total > maxNodes)
                {
                    return (total, $"The definition expands to more than {maxNodes} YAML nodes once its aliases " +
                                   $"are resolved; the limit is {maxNodes}. Write the repeated parts out instead of " +
                                   "nesting aliases.");
                }
            }
        }
        catch (YamlException)
        {
            // Not well-formed from here on: the parser refuses it at the same position and reads no further.
        }

        return (total, null);
    }

    /// <summary>
    ///     Counts the pipeline nodes and measures the nesting depth of node lists, following the node containers of
    ///     the pipeline SDK like <c>CountPipelineNodes</c>: root <c>triggers</c>, <c>transformations</c> and
    ///     <c>nodes</c>; inside a node its <c>transformations</c>, the <c>cases[].transformations</c> and the
    ///     <c>default</c> list of a Switch. Every key that matches a container name case-insensitively is followed.
    ///     Stops once either limit is exceeded.
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
        foreach (var container in new[] { "nodes", "transformations", "triggers" })
        {
            foreach (var rootList in Properties(root, container))
            {
                pending.Push((rootList, 1));
            }
        }

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

                foreach (var child in Properties(node, "transformations"))
                {
                    pending.Push((child, depth + 1));
                }

                foreach (var cases in Properties(node, "cases").OfType<JsonArray>())
                {
                    foreach (var switchCase in cases.OfType<JsonObject>())
                    {
                        foreach (var child in Properties(switchCase, "transformations"))
                        {
                            pending.Push((child, depth + 1));
                        }
                    }

                    foreach (var child in Properties(node, "default"))
                    {
                        pending.Push((child, depth + 1));
                    }
                }
            }
        }

        return (count, deepest);
    }

    // Every property whose name matches case-insensitively: JSON and the YAML converter keep "Transformations" and
    // "transformations" side by side, and the measurement must not stop at a decoy that only differs in case.
    private static IEnumerable<JsonNode?> Properties(JsonObject obj, string name) =>
        obj.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value);
}
