using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;
using Xunit;

namespace McpServices.Tests.Services;

public class ToolRiskRegistryTests
{
    [Fact]
    public void GetRiskLevel_KnownLowTool_ReturnsLow()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        registry.GetRiskLevel("sample_read").Should().Be(McpRiskLevel.Low);
    }

    [Fact]
    public void GetRiskLevel_KnownMediumTool_ReturnsMedium()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        registry.GetRiskLevel("sample_delete").Should().Be(McpRiskLevel.Medium);
    }

    [Fact]
    public void GetRiskLevel_KnownHighTool_ReturnsHigh()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        registry.GetRiskLevel("sample_drop_schema").Should().Be(McpRiskLevel.High);
    }

    [Fact]
    public void GetRiskLevel_UnknownTool_DefaultsToLow()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        registry.GetRiskLevel("not_registered").Should().Be(McpRiskLevel.Low);
    }

    [Fact]
    public void GetRiskLevel_ToolWithoutAttribute_DefaultsToLow()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        registry.GetRiskLevel("sample_default").Should().Be(McpRiskLevel.Low);
    }

    [Fact]
    public void GetAll_IncludesEveryToolInScannedAssembly()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        var all = registry.GetAll();

        all.Should().ContainKey("sample_read");
        all.Should().ContainKey("sample_default");
        all.Should().ContainKey("sample_delete");
        all.Should().ContainKey("sample_drop_schema");
    }

    [Fact]
    public void GetAllNonDefault_ExcludesUnannotatedTools()
    {
        var registry = new ToolRiskRegistry(typeof(SampleTools).Assembly);

        var nonDefault = registry.GetAllNonDefault();

        nonDefault.Should().ContainKey("sample_read");
        nonDefault.Should().ContainKey("sample_delete");
        nonDefault.Should().ContainKey("sample_drop_schema");
        nonDefault.Should().NotContainKey("sample_default");
    }

    [Fact]
    public void RealAssembly_RegistersGetToolRiskMetadataAsLow()
    {
        // Sanity check against the actual McpServices assembly — the registry-introspection
        // tool itself should be Low.
        var registry = new ToolRiskRegistry(typeof(IToolRiskRegistry).Assembly);

        registry.GetRiskLevel("get_tool_risk_metadata").Should().Be(McpRiskLevel.Low);
    }

    [Fact]
    public void RealAssembly_RegistersDeleteEntityAsMedium()
    {
        // Sanity check: the only annotated CRUD tool in the initial sweep.
        var registry = new ToolRiskRegistry(typeof(IToolRiskRegistry).Assembly);

        registry.GetRiskLevel("delete_entity").Should().Be(McpRiskLevel.Medium);
    }

    // ---------------------------------------------------------------------
    // Test seam: a fake tool type that the registry can scan.
    // ---------------------------------------------------------------------

    [McpServerToolType]
    public static class SampleTools
    {
        [McpServerTool(Name = "sample_read")]
        [McpRisk(McpRiskLevel.Low)]
        public static string SampleRead() => "ok";

        [McpServerTool(Name = "sample_default")]
        public static string SampleDefault() => "ok";

        [McpServerTool(Name = "sample_delete")]
        [McpRisk(McpRiskLevel.Medium)]
        public static string SampleDelete() => "ok";

        [McpServerTool(Name = "sample_drop_schema")]
        [McpRisk(McpRiskLevel.High)]
        public static string SampleDropSchema() => "ok";
    }
}
