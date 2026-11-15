using Json.Schema;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Builds the adapter's composite pipeline JSON Schema for <c>validate_pipeline_definition</c> without
///     retaining it in the process (AB#9015).
///     <para>
///     JsonSchema.Net registers every schema it builds in <see cref="BuildOptions.SchemaRegistry" />, which
///     defaults to the process-wide <see cref="SchemaRegistry.Global" />. The composite schema carries no
///     <c>$id</c> (the generator strips it), so each build got a fresh random base URI and stayed in the
///     global registry for the life of the pod: every validation call leaked the whole compiled schema and
///     the MCP pod was OOM-killed during agent runs. A per-call registry keeps the schema collectable; it
///     still falls back to the global registry for the JSON Schema meta-schemas.
///     </para>
/// </summary>
internal static class PipelineSchemaCompiler
{
    /// <summary>
    ///     Builds a schema from <paramref name="schemaJson" /> into a registry owned by this call only.
    /// </summary>
    public static JsonSchema Compile(string schemaJson) =>
        JsonSchema.FromText(schemaJson, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}
