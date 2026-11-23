using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Json.Schema;
using McpServices.Tests.Tools;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.Serialization;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     validate_pipeline_definition evaluates caller-supplied drafts within bounded memory and time. The composite
///     schema of the pipeline SDK is a <c>oneOf</c> over every node schema, and child-capable nodes refer back to it;
///     with the list output format every nested level of control nodes multiplied the kept results by about ten, so a
///     draft with If nodes nested four levels deep exhausted the process. These tests pin the dispatch on the node
///     type, the verdict parity with the raw union, and the limits checked before any evaluation.
/// </summary>
[Collection(ProcessMemoryMeasurement.Name)]
public class PipelineValidationBoundsTests : ToolTestBase
{
    private const string AdapterId = "bbbbbbbbbbbbbbbbbbbbbbbb";

    // Generous for a shared CI agent; the evaluation itself takes milliseconds. Before the fix depth 3 took over a
    // second and allocated about 1 GB, depth 4 ran out of memory.
    private static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(10);
    private const long AllocationBound = 400L * 1024 * 1024;

    public PipelineValidationBoundsTests()
    {
        GivenAuthenticated();
    }

    // ===== the dispatch on the node type ==========================================================

    [Fact]
    public void Compile_GeneratorShapedSchema_RewritesTheNodeUnionsIntoATypeDispatch()
    {
        var root = JsonNode.Parse(GeneratorShapedPipelineSchema.Build(plainNodes: 3, childCapableNodes: 2))!
            .AsObject();

        PipelineSchemaCompiler.Discriminate(root).Should().BeTrue();

        var transformation = root["$defs"]!["TransformationNode"]!.AsObject();
        transformation.ContainsKey("oneOf").Should().BeFalse();
        transformation["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("type");
        // If@1, SetPrimitiveValue@1, one more control node and two more plain nodes
        transformation["properties"]!["type"]!["enum"]!.AsArray().Should().HaveCount(5);
        transformation["allOf"]!.AsArray().Should().HaveCount(5);
        transformation["allOf"]![0]!["if"]!["required"]!.AsArray().Select(r => r!.GetValue<string>())
            .Should().Equal("type");
        root["$defs"]!["TriggerNode"]!.AsObject().ContainsKey("allOf").Should().BeTrue();
        PipelineSchemaCompiler.Compile(GeneratorShapedPipelineSchema.Build(3, 2)).Discriminated.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"oneOf":[{"type":"object","required":["type"],"properties":{"type":{"type":"string"}}}]}""")]
    [InlineData("""{"oneOf":[{"type":"object","properties":{"type":{"const":"A@1"}}}]}""")]
    [InlineData("""{"oneOf":[{"required":["type"],"properties":{"type":{"const":"A@1"}}}]}""")]
    [InlineData("""{"oneOf":[{"type":"object","required":["type"],"properties":{"type":{"const":"A@1"}}},{"type":"object","required":["type"],"properties":{"type":{"const":"A@1"}}}]}""")]
    [InlineData("""{"oneOf":[{"type":"object","required":["type"],"properties":{"type":{"const":"A@1"}}}],"minProperties":1}""")]
    [InlineData("""{"oneOf":[{"$ref":"#/$defs/A"}]}""")]
    [InlineData("""{"anyOf":[{"type":"object","required":["type"],"properties":{"type":{"const":"A@1"}}}]}""")]
    public void Discriminate_OtherShapes_StayUntouched(string definition)
    {
        var root = new JsonObject { ["$defs"] = new JsonObject { ["Node"] = JsonNode.Parse(definition) } };
        var before = root.ToJsonString();

        PipelineSchemaCompiler.Discriminate(root).Should().BeFalse();
        root.ToJsonString().Should().Be(before);
    }

    public static TheoryData<string> ParityDrafts() => new()
    {
        GeneratorShapedPipelineSchema.NestedIfDraft(2),
        GeneratorShapedPipelineSchema.NestedIfDraft(2, breakInnermost: true),
        "transformations:\n  - type: Unknown@1\n",
        "transformations:\n  - valueText: no type\n",
        "transformations:\n  - type: 42\n",
        "transformations:\n  - just a string\n",
        "transformations:\n  - null\n",
        "transformations:\n  - type: SetPrimitiveValue@1\n",
        "transformations:\n  - type: SetPrimitiveValue@1\n    valueText: x\n    extra: 1\n",
        "transformations:\n  - type: If@1\n    transformations:\n      - type: Unknown@1\n",
        "transformations:\n  - type: If@1\n    transformations:\n      - valueText: missing type\n",
        "triggers:\n  - type: SetPrimitiveValue@1\n    valueText: a transformation as trigger\n",
        "triggers:\n  - type: FromHttpRequest@2\n    path: /x\ntransformations: []\n",
    };

    [Theory]
    [MemberData(nameof(ParityDrafts))]
    public void Discriminated_Schema_GivesTheSameVerdictAsTheRawUnion(string draftYaml)
    {
        var schemaJson = GeneratorShapedPipelineSchema.Build(plainNodes: 8, childCapableNodes: 3);
        var raw = JsonSchema.FromText(schemaJson, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var discriminated = PipelineSchemaCompiler.Compile(schemaJson);
        discriminated.Discriminated.Should().BeTrue();

        using var doc = JsonDocument.Parse(YamlToJsonConverter.ToJsonNodeAutoDetect(draftYaml)!.ToJsonString());
        var flag = new EvaluationOptions { OutputFormat = OutputFormat.Flag };

        discriminated.Schema.Evaluate(doc.RootElement, flag).IsValid
            .Should().Be(raw.Evaluate(doc.RootElement, flag).IsValid);
    }

    // ===== the crash shape: nested If nodes ======================================================

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ValidatePipelineDefinition_NestedIfNodes_ValidatesWithinBoundedTimeAndMemory(int depth)
    {
        GivenSchema(GeneratorShapedPipelineSchema.Build());
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(depth);

        var (result, elapsed, allocated) = await Measure(() =>
            PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}")));
        result.Errors.Should().BeEmpty();
        result.NodeCount.Should().Be(1 + depth * 5 + 1);
        elapsed.Should().BeLessThan(TimeBound);
        allocated.Should().BeLessThan(AllocationBound);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ValidatePipelineDefinition_NestedIfNodesInvalidAtTheDeepestLevel_ReportsThatNodeOnly(int depth)
    {
        GivenSchema(GeneratorShapedPipelineSchema.Build());
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(depth, breakInnermost: true);

        var (result, elapsed, allocated) = await Measure(() =>
            PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.IsValid.Should().BeFalse();
        // The broken node is the last sibling before the innermost If: /transformations/4/transformations/4/.../3
        var brokenPath = string.Concat(Enumerable.Repeat("/transformations/4", depth - 1)) + "/transformations/3";
        result.Errors.Should().Contain(e => e.Path == brokenPath && e.Keyword == "required");
        result.Errors.Should().NotContain(e => e.SchemaPath != null && e.SchemaPath.Contains("/if/"),
            "a failing if only selects the node type and is no finding");
        result.Errors.Should().OnlyContain(e => brokenPath.StartsWith(e.Path) || e.Path.StartsWith(brokenPath + "/"),
            "only the broken node, its ancestors and its own properties carry findings");
        elapsed.Should().BeLessThan(TimeBound);
        allocated.Should().BeLessThan(AllocationBound);
    }

    // ===== limits checked before the evaluation ==================================================

    [Fact]
    public async Task ValidatePipelineDefinition_TooLongText_IsRefusedBeforeTheSchemaIsFetched()
    {
        var draft = "description: " + new string('x', PipelineDefinitionLimits.MaxDefinitionLength) + "\n";

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft);

        AssertRefused(result, PipelineDefinitionLimits.MaxDefinitionLength.ToString());
        MockCommunicationClient.Verify(c => c.GetPipelineSchemaAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_YamlAliasBomb_IsRefusedBeforeItIsExpanded()
    {
        // Nine levels of ten aliases each expand to 10^9 nodes; the document itself is under 1 KB.
        var sb = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i <= 9; i++)
        {
            var alias = $"*a{i - 1}";
            sb.AppendLine($"a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat(alias, 10))}]");
        }

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, sb.ToString());

        AssertRefused(result, PipelineDefinitionLimits.MaxExpandedYamlNodes.ToString());
        MockCommunicationClient.Verify(c => c.GetPipelineSchemaAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_TooManyNodes_IsRefusedWithoutEvaluation()
    {
        GivenSchema(GeneratorShapedPipelineSchema.Build());
        var sb = new StringBuilder("transformations:\n");
        for (var i = 0; i <= PipelineDefinitionLimits.MaxPipelineNodes; i++)
        {
            sb.AppendLine($"  - type: {GeneratorShapedPipelineSchema.SetValueNode}");
        }

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, sb.ToString());

        AssertRefused(result, PipelineDefinitionLimits.MaxPipelineNodes.ToString());
    }

    [Fact]
    public async Task ValidatePipelineDefinition_TooDeeplyNestedNodeLists_AreRefusedWithoutEvaluation()
    {
        GivenSchema(GeneratorShapedPipelineSchema.Build());
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(PipelineDefinitionLimits.MaxNestingDepth + 1,
            siblings: 0);

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft);

        AssertRefused(result, PipelineDefinitionLimits.MaxNestingDepth.ToString());
    }

    [Fact]
    public async Task ValidatePipelineDefinition_NestingAtTheLimit_IsEvaluated()
    {
        GivenSchema(GeneratorShapedPipelineSchema.Build());
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(PipelineDefinitionLimits.MaxNestingDepth - 1,
            siblings: 1);

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft);

        result.ErrorCode.Should().BeNull();
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("a: [x, y, {b: c}]\n", 8)]
    [InlineData("a: &n [x, y]\nb: *n\nc: *n\n", 13)]
    [InlineData("a: &s x\nb: *s\n", 5)]
    [InlineData("{\"a\": [1, 2]}", 5)]
    public void ScanYaml_CountsAliasesAtTheSizeOfTheirAnchor(string yaml, long expected)
    {
        var (expanded, refusal) = PipelineDefinitionLimits.ScanYaml(yaml, long.MaxValue, 256);

        refusal.Should().BeNull();
        expanded.Should().Be(expected);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_RecursiveAnchor_IsRefusedBeforeItIsLoaded()
    {
        // The alias sits inside its own anchor: the loader keeps the cycle and the converter would copy the large
        // sibling once per level until its depth limit.
        var draft = $"a: &a [&b [{string.Join(", ", Enumerable.Repeat("x", 1000))}], *a]\n";

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft);

        AssertRefused(result, "*a ");
        MockCommunicationClient.Verify(c => c.GetPipelineSchemaAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_AliasBeforeItsAnchor_IsRefused()
    {
        // The alias bomb written in reverse order: each level aliases the level defined below it.
        var sb = new StringBuilder();
        for (var i = 9; i >= 1; i--)
        {
            sb.AppendLine($"a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 10))}]");
        }

        sb.AppendLine("a0: &a0 [x, x, x, x, x, x, x, x, x, x]");

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, sb.ToString());

        AssertRefused(result, "*a8 ");
    }

    [Theory]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    public async Task ValidatePipelineDefinition_DeeplyNestedDocument_IsRefusedBeforeTheRecursiveLoad(
        char open, char close)
    {
        // 50,000 levels would overflow the stack of the recursive YAML loader (an uncatchable crash).
        var draft = new string(open, 50_000) + new string(close, 50_000);

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, draft);

        AssertRefused(result, PipelineDefinitionLimits.MaxDocumentDepth.ToString());
    }

    [Fact]
    public void MeasureNodes_FollowsEveryCaseVariantOfAContainer()
    {
        var definition = JsonNode.Parse("""
            {"Transformations": [], "transformations": [
              {"type": "If@1", "Transformations": [], "transformations": [
                {"type": "If@1", "transformations": [{"type": "X@1"}]}]}]}
            """);

        PipelineDefinitionLimits.MeasureNodes(definition, 100, 100).Should().Be((3, 3));
    }

    [Theory]
    [InlineData("/properties/transformations/items/$ref/allOf/3/if", true)]
    [InlineData("/properties/transformations/items/$ref/allOf/3/if/properties/type", true)]
    [InlineData("/properties/transformations/items/$ref/allOf/3/then/required", false)]
    [InlineData("/properties/if/type", false)]
    [InlineData("", false)]
    public void IsInsideIf_SkipsOnlyTheIfKeyword(string evaluationPath, bool expected)
    {
        PipelineSchemaEvaluator.IsInsideIf(evaluationPath).Should().Be(expected);
    }

    // ===== the evaluation budget =================================================================

    [Fact]
    public async Task EvaluateAsync_BudgetExceeded_ReturnsTheBudgetCodeInsteadOfWaiting()
    {
        var compiled = PipelineSchemaCompiler.Compile(GeneratorShapedPipelineSchema.Build());
        using var doc = JsonDocument.Parse(YamlToJsonConverter
            .ToJsonNodeAutoDetect(GeneratorShapedPipelineSchema.NestedIfDraft(16, breakInnermost: true))!
            .ToJsonString());

        var outcome = await PipelineSchemaEvaluator.EvaluateAsync(compiled, doc.RootElement, 16, TimeSpan.Zero);

        outcome.ErrorCode.Should().Be(PipelineSchemaEvaluator.BudgetExceededCode);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_UnrewritableRecursiveUnion_RefusesNestedDefinitions()
    {
        // Without a const discriminator the node union cannot be rewritten and stays a self-referencing oneOf,
        // whose cost is exponential in the nesting depth even with the flag output format.
        var schemaJson = GeneratorShapedPipelineSchema.Build(4, 2).Replace("\"const\"", "\"default\"");
        var compiled = PipelineSchemaCompiler.Compile(schemaJson);
        compiled.Discriminated.Should().BeFalse();
        compiled.RecursiveUnions.Should().BeTrue();
        GivenSchema(schemaJson);

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId,
            GeneratorShapedPipelineSchema.NestedIfDraft(PipelineSchemaEvaluator.MaxDepthForRecursiveUnions));

        AssertRefused(result, PipelineSchemaEvaluator.MaxDepthForRecursiveUnions.ToString());
    }

    [Fact]
    public async Task ValidatePipelineDefinition_UnrewritableRecursiveUnion_EvaluatesFlatDefinitions()
    {
        var schemaJson = GeneratorShapedPipelineSchema.Build(4, 2).Replace("\"const\"", "\"default\"");
        GivenSchema(schemaJson);

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId,
            $"transformations:\n  - type: {GeneratorShapedPipelineSchema.SetValueNode}\n    valueText: x\n");

        result.ErrorCode.Should().BeNull();
        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeTrue("additionalProperties leaves only the SetPrimitiveValue schema matching");
    }

    [Theory]
    // indirect: two branches are $refs to nodes whose transformations refer back to the union
    [InlineData("""{"$defs":{"U":{"oneOf":[{"$ref":"#/$defs/IfNode"},{"$ref":"#/$defs/ForEachNode"}]},"IfNode":{"properties":{"transformations":{"items":{"$ref":"#/$defs/U"}}}},"ForEachNode":{"properties":{"transformations":{"items":{"$ref":"#/$defs/U"}}}}}}""", true)]
    // a definition name that needs pointer escaping
    [InlineData("""{"$defs":{"a/b":{"anyOf":[{"properties":{"t":{"items":{"$ref":"#/$defs/a~1b"}}}},{"properties":{"u":{"items":{"$ref":"#/$defs/a~1b"}}}}]}}}""", true)]
    // a reference this check cannot resolve fails closed
    [InlineData("""{"$defs":{"U":{"oneOf":[{"$ref":"https://example.com/a.json"},{"$ref":"https://example.com/b.json"}]}}}""", true)]
    [InlineData("""{"properties":{"t":{"oneOf":[{"items":{"$ref":"#"}},{"$dynamicRef":"#node"}]}}}""", true)]
    // one way back is linear: the nullable self-reference of a recursive data type
    [InlineData("""{"$defs":{"Json":{"properties":{"parent":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/Json"}]}}}}}""", false)]
    // unions without a way back
    [InlineData("""{"$defs":{"U":{"oneOf":[{"$ref":"#/$defs/A"},{"type":"string"}]},"A":{"type":"object"}}}""", false)]
    [InlineData("""{"properties":{"v":{"oneOf":[{"type":"string"},{"type":"number"}]}}}""", false)]
    public void HasRecursiveUnions_FindsEveryWayBack(string schemaJson, bool expected)
    {
        PipelineSchemaCompiler.HasRecursiveUnions(JsonNode.Parse(schemaJson)!.AsObject()).Should().Be(expected);
    }

    [Fact]
    public void Compile_GeneratorShapedSchema_LeavesNoRecursiveUnion()
    {
        PipelineSchemaCompiler.Compile(GeneratorShapedPipelineSchema.Build()).RecursiveUnions.Should().BeFalse();
    }

    [Fact]
    public async Task ValidatePipelineDefinition_RecursiveConfigDataAtTheDocumentDepthLimit_DoesNotOverflowTheStack()
    {
        // A node whose config type refers to itself through a nullable property (a JSON node with a parent), nested
        // as deep as the document limit allows. On a thread-pool thread this overflowed the stack (process crash).
        const string schemaJson = """
            {"type":"object","properties":{"transformations":{"type":"array","items":{"$ref":"#/$defs/TransformationNode"}}},
             "$defs":{
               "TransformationNode":{"oneOf":[{"type":"object","required":["type"],
                 "properties":{"type":{"const":"FromPolling@1"},"body":{"$ref":"#/$defs/JsonNode"}}}]},
               "JsonNode":{"type":"object","properties":{"parent":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/JsonNode"}]}}}}}
            """;
        GivenSchema(schemaJson);
        // root mapping, transformations sequence, node mapping, body: 4 levels before the parent chain
        var levels = PipelineDefinitionLimits.MaxDocumentDepth - 5;
        var json = "{\"transformations\":[{\"type\":\"FromPolling@1\",\"body\":" +
                   string.Concat(Enumerable.Repeat("{\"parent\":", levels)) + "null" + new string('}', levels) + "}]}";

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, json);

        result.ErrorCode.Should().BeNull(result.Message);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task RunOnLargeStack_PassesResultsAndExceptionsThrough()
    {
        (await PipelineSchemaEvaluator.RunOnLargeStack(() => 42)).Should().Be(42);
        var failing = () => PipelineSchemaEvaluator.RunOnLargeStack<int>(() => throw new InvalidOperationException("x"));
        await failing.Should().ThrowAsync<InvalidOperationException>();
    }

    private void GivenSchema(string schemaJson) =>
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId)).ReturnsAsync(schemaJson);

    private static void AssertRefused(
        Meshmakers.Octo.Backend.McpServices.Models.ValidatePipelineDefinitionResponse result, string limit)
    {
        result.IsSuccess.Should().BeTrue("the tool call completed; the definition is refused");
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be(PipelineDefinitionLimits.RefusedCode);
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain(limit);
    }

    private static async Task<(T Result, TimeSpan Elapsed, long Allocated)> Measure<T>(Func<Task<T>> action)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();
        return (result, sw.Elapsed, GC.GetTotalAllocatedBytes(true) - before);
    }
}
