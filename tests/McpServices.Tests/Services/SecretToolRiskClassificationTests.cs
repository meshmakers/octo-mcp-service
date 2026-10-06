using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#5543 (concept §4.7): tools that set secrets are high risk. The registry is static (one level per tool),
///     so secret writes live in dedicated high-risk tools and the generic writes refuse secret values.
/// </summary>
public class SecretToolRiskClassificationTests
{
    private static readonly ToolRiskRegistry Registry = new();

    [Theory]
    [InlineData("set_entity_secrets")]
    [InlineData("create_entity_with_secrets")]
    [InlineData("start_secret_sweep")]
    [InlineData("update_identity_provider")]
    [InlineData("add_oauth_identity_provider")]
    [InlineData("add_azure_entra_id_identity_provider")]
    public void SecretWritingTools_AreHighRisk(string tool)
    {
        Registry.GetRiskLevel(tool).Should().Be(McpRiskLevel.High);
    }

    [Theory]
    [InlineData("create_entity")]
    [InlineData("update_entity")]
    public void GenericWriteTools_StayMedium_BecauseTheyRefuseSecretValues(string tool)
    {
        Registry.GetRiskLevel(tool).Should().Be(McpRiskLevel.Medium);
    }

    [Fact]
    public void GetSecretStatus_IsReadOnlyLow()
    {
        Registry.GetAll().Should().ContainKey("get_secret_status");
        Registry.GetRiskLevel("get_secret_status").Should().Be(McpRiskLevel.Low);
    }

    [Fact]
    public void GetSecretInventory_IsReadOnlyLow()
    {
        Registry.GetAll().Should().ContainKey("get_secret_inventory");
        Registry.GetRiskLevel("get_secret_inventory").Should().Be(McpRiskLevel.Low);
    }
}
