using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>AB#7429: the quantity and request-versus-limit rules the MCP tools check before it writes anything.</summary>
public sealed class WorkloadResourceSettingsTests
{
    [Theory]
    [InlineData("250m", 0.25)]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1)]
    [InlineData("1500m", 1.5)]
    [InlineData("128974848", 128974848)]
    [InlineData("129e6", 129000000)]
    [InlineData("1k", 1000)]
    [InlineData("1Ki", 1024)]
    [InlineData("512Mi", 536870912)]
    [InlineData("1Gi", 1073741824)]
    [InlineData("2G", 2000000000)]
    public void Quantity_Parses(string text, double expected)
    {
        Assert.True(KubernetesQuantity.TryParse(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("512 MB")]
    [InlineData("512MB")]
    [InlineData("Mi")]
    [InlineData("1.2.3")]
    [InlineData("-1")]
    [InlineData("1mi")]
    [InlineData("abc")]
    [InlineData("1e\u0663")]
    [InlineData("\u0663")]
    [InlineData("")]
    [InlineData(null)]
    public void Quantity_Rejects(string? text) => Assert.False(KubernetesQuantity.TryParse(text, out _));

    [Fact]
    public void InvalidQuantity_IsNamedWithAttributeAndValue()
    {
        var errors = new WorkloadResourceSettings(null, null, "512 MB", null).Validate();

        Assert.Equal(["MemoryRequest '512 MB' is not a valid Kubernetes quantity"], errors);
    }

    [Fact]
    public void MemoryRequestAboveLimit_IsRejected()
    {
        var errors = new WorkloadResourceSettings(null, null, "2Gi", "1Gi").Validate();

        Assert.Equal(["MemoryRequest 2Gi exceeds MemoryLimit 1Gi"], errors);
    }

    [Fact]
    public void CpuRequestAboveLimit_IsRejected()
    {
        var errors = new WorkloadResourceSettings("500m", "250m", null, null).Validate();

        Assert.Equal(["CpuRequest 500m exceeds CpuLimit 250m"], errors);
    }

    [Fact]
    public void RequestAndLimit_AreComparedAsQuantities_So1024MiEquals1Gi()
    {
        Assert.Empty(new WorkloadResourceSettings(null, null, "1024Mi", "1Gi").Validate());
        Assert.Empty(new WorkloadResourceSettings("0.5", "500m", null, null).Validate());
    }

    [Fact]
    public void ValidValuesAndAllEmpty_PassValidation()
    {
        Assert.Empty(new WorkloadResourceSettings("100m", null, "512Mi", "1Gi").Validate());
        Assert.Empty(new WorkloadResourceSettings(null, null, null, null).Validate());
    }

    [Fact]
    public void RequestWithoutLimit_IsNotCompared()
    {
        Assert.Empty(new WorkloadResourceSettings("4", null, "64Gi", null).Validate());
    }

    [Fact]
    public void Violations_AreAllReported()
    {
        var errors = new WorkloadResourceSettings("x", null, "2Gi", "1Gi").Validate();

        Assert.Equal(2, errors.Count);
        Assert.Contains("CpuRequest 'x' is not a valid Kubernetes quantity", errors);
        Assert.Contains("MemoryRequest 2Gi exceeds MemoryLimit 1Gi", errors);
    }
}
