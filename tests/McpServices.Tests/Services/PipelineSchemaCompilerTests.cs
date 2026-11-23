using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Json.Schema;
using McpServices.Tests.Tools;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#9015: validate_pipeline_definition built the adapter's composite schema with the default
///     JsonSchema.Net options, which register it in the process-wide <see cref="SchemaRegistry.Global" />.
///     The schema has no <c>$id</c>, so every call registered a new copy and the MCP pod grew until it was
///     OOM-killed (measured: 100 calls with a 0.7 MB schema retained ~680 MB). These tests guard that a
///     compiled schema is collectable once the call is over.
/// </summary>
[Collection(ProcessMemoryMeasurement.Name)]
public class PipelineSchemaCompilerTests : ToolTestBase
{
    private const string AdapterId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    public PipelineSchemaCompilerTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public void Compile_DoesNotRegisterTheSchemaInTheGlobalRegistry()
    {
        var weak = CompileAndForget(BuildCompositeSchema(5));

        ForceFullCollection();

        weak.IsAlive.Should().BeFalse(
            "a schema registered in SchemaRegistry.Global stays reachable for the life of the process");
    }

    [Fact]
    public void Compile_ResolvesLocalDefsReferences()
    {
        var schema = PipelineSchemaCompiler.Compile(BuildCompositeSchema(3)).Schema;

        using var valid = JsonDocument.Parse("""{"name":"p","nodes":[{"p0":"x"}]}""");
        using var invalid = JsonDocument.Parse("""{"name":"p","nodes":[{"p0":42}]}""");

        schema.Evaluate(valid.RootElement).IsValid.Should().BeTrue();
        schema.Evaluate(invalid.RootElement).IsValid.Should().BeFalse("p0 is a string in every $defs entry");
    }

    [Fact]
    public async Task ValidatePipelineDefinition_RepeatedCalls_DoNotRetainTheSchema()
    {
        // ~250 KB schema; before the fix each call retained a few MB, so 40 calls retained > 100 MB.
        var schemaJson = BuildCompositeSchema(100);
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId)).ReturnsAsync(schemaJson);

        // Warm-up: JIT, JsonSchema.Net's meta-schemas and keyword caches are allocated once.
        await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, "name: p\nnodes: []");
        var before = ForceFullCollection();

        for (var i = 0; i < 40; i++)
        {
            var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId,
                "name: p\nnodes:\n  - p0: x");
            result.IsSuccess.Should().BeTrue();
            result.IsValid.Should().BeTrue();
        }

        var retained = ForceFullCollection() - before;

        retained.Should().BeLessThan(20L * 1024 * 1024,
            "a validation call must not keep its compiled schema alive (AB#9015)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompileAndForget(string schemaJson) =>
        new(PipelineSchemaCompiler.Compile(schemaJson).Schema);

    private static long ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    ///     A schema shaped like the adapter's composite pipeline schema: no <c>$id</c>, node schemas under
    ///     <c>$defs</c>, referenced from <c>nodes[]</c> by local <c>$ref</c>.
    /// </summary>
    private static string BuildCompositeSchema(int nodeTypes)
    {
        var defs = new JsonObject();
        var anyOf = new JsonArray();
        for (var i = 0; i < nodeTypes; i++)
        {
            var properties = new JsonObject();
            for (var p = 0; p < 20; p++)
            {
                properties[$"p{p}"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = new string('d', 80)
                };
            }

            defs[$"Node{i}"] = new JsonObject { ["type"] = "object", ["properties"] = properties };
            anyOf.Add(new JsonObject { ["$ref"] = $"#/$defs/Node{i}" });
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["required"] = new JsonArray("name", "nodes"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["nodes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = anyOf } }
            },
            ["$defs"] = defs
        }.ToJsonString();
    }
}

/// <summary>Serialises tests that measure process-wide managed memory, so parallel tests do not skew them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessMemoryMeasurement
{
    public const string Name = "Process memory measurement";
}
