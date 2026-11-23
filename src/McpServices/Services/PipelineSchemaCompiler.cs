using System.Text.Json.Nodes;
using Json.Schema;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Builds the adapter's composite pipeline JSON Schema for <c>validate_pipeline_definition</c> without
///     retaining it in the process, and in a shape whose evaluation cost grows linearly with the definition.
///     <para>
///     Retention: JsonSchema.Net registers every schema it builds in <see cref="BuildOptions.SchemaRegistry" />,
///     which defaults to the process-wide <see cref="SchemaRegistry.Global" />. The composite schema carries no
///     <c>$id</c> (the generator strips it), so each build got a fresh random base URI and stayed in the
///     global registry for the life of the pod: every validation call leaked the whole compiled schema and
///     the MCP pod was OOM-killed during agent runs. A per-call registry keeps the schema collectable; it
///     still falls back to the global registry for the JSON Schema meta-schemas.
///     </para>
///     <para>
///     Cost: the pipeline SDK's generator emits <c>$defs/TransformationNode</c> (and <c>TriggerNode</c>) as a
///     <c>oneOf</c> over every node schema, and every child-capable node refers back to
///     <c>TransformationNode</c> for its nested <c>transformations</c>. A <c>oneOf</c> evaluates every branch, so
///     each level of nested control nodes (If inside If inside If, ...) is evaluated once per child-capable
///     node schema; with the list output format the evaluator keeps all of those results, and the memory grows
///     by about one order of magnitude per nesting level (four levels exhausted the MCP process).
///     <see cref="Discriminate" /> rewrites such a <c>oneOf</c> into a dispatch on the node's <c>type</c>
///     discriminator, so only the schema of the node's own type is evaluated. The verdict stays the same: every
///     branch requires <c>type</c> with its own <c>const</c>, so at most one branch can match, and the
///     <c>oneOf</c> holds exactly when the branch of the node's type holds.
///     </para>
/// </summary>
internal static class PipelineSchemaCompiler
{
    /// <summary>The result of <see cref="Compile" />.</summary>
    /// <param name="Schema">The compiled schema.</param>
    /// <param name="Discriminated">
    ///     True when at least one node <c>oneOf</c> was rewritten into a dispatch on <c>type</c>; false when the
    ///     schema has no part of the generator's shape and is evaluated as it came.
    /// </param>
    /// <param name="RecursiveUnions">
    ///     True when, after the rewrite, a definition is still a <c>oneOf</c> / <c>anyOf</c> that refers to itself
    ///     from one of its branches. Evaluating nested instances against such a union costs time and memory
    ///     exponential in the nesting depth, in every output format: the evaluator walks each branch's properties
    ///     (the nested list included) before it reaches the failing discriminator.
    /// </param>
    public sealed record CompiledPipelineSchema(JsonSchema Schema, bool Discriminated, bool RecursiveUnions);

    private static readonly string[] DefinitionContainers = ["$defs", "definitions"];

    // Keys a generator node-union definition may carry besides oneOf; anything else leaves it untouched.
    private static readonly HashSet<string> UnionAnnotationKeys =
        new(StringComparer.Ordinal) { "oneOf", "title", "description", "$comment" };

    /// <summary>
    ///     Builds a schema from <paramref name="schemaJson" /> into a registry owned by this call only, after
    ///     rewriting the generator's node unions (<see cref="Discriminate" />).
    /// </summary>
    public static CompiledPipelineSchema Compile(string schemaJson)
    {
        var discriminated = false;
        var recursiveUnions = false;
        var text = schemaJson;
        if (JsonNode.Parse(schemaJson) is JsonObject root)
        {
            discriminated = Discriminate(root);
            if (discriminated)
            {
                text = root.ToJsonString();
            }

            recursiveUnions = HasRecursiveUnions(root);
        }

        return new CompiledPipelineSchema(
            JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = new SchemaRegistry() }), discriminated,
            recursiveUnions);
    }

    /// <summary>
    ///     Rewrites every definition under <c>$defs</c> / <c>definitions</c> of <paramref name="root" /> that is a
    ///     node union of the generator's shape — <c>{ "oneOf": [ ... ] }</c> whose branches are all objects of
    ///     <c>"type": "object"</c> that require <c>type</c> and fix it with distinct string <c>const</c> values —
    ///     into
    ///     <code>
    ///     { "type": "object", "required": ["type"], "properties": { "type": { "enum": [ ...consts ] } },
    ///       "allOf": [ { "if": { "required": ["type"], "properties": { "type": { "const": c } } }, "then": branch }, ... ] }
    ///     </code>
    ///     A definition of any other shape stays as it is.
    /// </summary>
    /// <returns>True when at least one definition was rewritten.</returns>
    internal static bool Discriminate(JsonObject root)
    {
        var rewritten = false;
        foreach (var containerName in DefinitionContainers)
        {
            if (root[containerName] is not JsonObject container)
            {
                continue;
            }

            foreach (var name in container.Select(p => p.Key).ToList())
            {
                if (container[name] is JsonObject definition && TryDiscriminate(definition, out var replacement))
                {
                    container[name] = replacement;
                    rewritten = true;
                }
            }
        }

        return rewritten;
    }

    /// <summary>
    ///     True when a definition under <c>$defs</c> / <c>definitions</c> of <paramref name="root" /> is a
    ///     <c>oneOf</c> or <c>anyOf</c> whose branches refer to the definition itself (the generator's node union that
    ///     <see cref="Discriminate" /> could not rewrite).
    /// </summary>
    internal static bool HasRecursiveUnions(JsonObject root)
    {
        foreach (var containerName in DefinitionContainers)
        {
            if (root[containerName] is not JsonObject container)
            {
                continue;
            }

            foreach (var (name, definition) in container)
            {
                var selfReference = $"\"#/{containerName}/{name}\"";
                foreach (var keyword in new[] { "oneOf", "anyOf" })
                {
                    if (definition?[keyword] is JsonArray branches &&
                        branches.ToJsonString().Contains(selfReference, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryDiscriminate(JsonObject definition, out JsonObject replacement)
    {
        replacement = null!;
        if (definition["oneOf"] is not JsonArray { Count: > 0 } branches ||
            definition.Any(p => !UnionAnnotationKeys.Contains(p.Key)))
        {
            return false;
        }

        var discriminators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            if (DiscriminatorOf(branch) is not { } value || !discriminators.Add(value))
            {
                return false;
            }
        }

        var allOf = new JsonArray();
        var values = new JsonArray();
        foreach (var branch in branches)
        {
            var value = DiscriminatorOf(branch)!;
            values.Add(value);
            allOf.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["required"] = new JsonArray("type"),
                    ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = value } },
                },
                ["then"] = branch!.DeepClone(),
            });
        }

        replacement = new JsonObject();
        foreach (var key in new[] { "title", "description", "$comment" })
        {
            if (definition[key] is { } annotation)
            {
                replacement[key] = annotation.DeepClone();
            }
        }

        replacement["type"] = "object";
        replacement["required"] = new JsonArray("type");
        replacement["properties"] = new JsonObject { ["type"] = new JsonObject { ["enum"] = values } };
        replacement["allOf"] = allOf;
        return true;
    }

    // The string const of a generator node schema's "type" property, or null when the branch is not of that
    // shape (not an object schema, "type" not required, or no string const).
    private static string? DiscriminatorOf(JsonNode? branch)
    {
        if (branch is not JsonObject schema ||
            schema["type"] is not JsonValue schemaType || !schemaType.TryGetValue<string>(out var typeName) ||
            typeName != "object" ||
            schema["required"] is not JsonArray required ||
            !required.Any(r => r is JsonValue v && v.TryGetValue<string>(out var s) && s == "type") ||
            schema["properties"] is not JsonObject properties ||
            properties["type"] is not JsonObject typeProperty ||
            typeProperty["const"] is not JsonValue constValue ||
            !constValue.TryGetValue<string>(out var discriminator))
        {
            return null;
        }

        return discriminator;
    }
}
