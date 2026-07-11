using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
/// Pins the startup fail-fast on <see cref="McpServiceOptions.PublicUrl" />. PublicUrl is the RFC 9728
/// resource identifier the MCP challenge scheme advertises and the RFC 8707 resource indicator
/// interactive clients (Claude Code) send; a missing or relative value silently breaks login with
/// invalid_target, so the service must refuse to start.
/// </summary>
public class ValidateMcpServiceOptionsTests
{
    private static ValidateMcpServiceOptions Sut => new();

    [Theory]
    [InlineData("https://mcp.test-2.mm.cloud")]
    [InlineData("https://localhost:5017")]
    [InlineData("http://localhost:5017")]
    public void Validate_WithAbsoluteHttpUrl_Succeeds(string publicUrl)
    {
        var result = Sut.Validate(null, new McpServiceOptions { PublicUrl = publicUrl });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mcp.example.com")]      // no scheme — not absolute
    [InlineData("/relative/path")]       // relative
    [InlineData("ftp://mcp.example.com")] // wrong scheme
    public void Validate_WithInvalidPublicUrl_Fails(string publicUrl)
    {
        var result = Sut.Validate(null, new McpServiceOptions { PublicUrl = publicUrl });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("PublicUrl");
    }
}
