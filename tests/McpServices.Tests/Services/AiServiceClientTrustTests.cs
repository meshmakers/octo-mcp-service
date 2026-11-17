using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     The one cross-tenant pair shared by the in-tool gate (AB#9189) and the HTTP route guard (AB#9188): the AI service
///     client, exact id, with a token of the system tenant.
/// </summary>
public class AiServiceClientTrustTests
{
    private static McpCallerPrincipal Service(string? clientId, string? tenantId) => new(null, clientId, tenantId, []);

    [Theory]
    [InlineData("octo-ai-adapter", "OctoSystem", true)]
    [InlineData("octo-ai-adapter", "octosystem", true)]
    [InlineData("OCTO-AI-ADAPTER", "OctoSystem", false)]
    [InlineData("octo-ai-adapter", "tenant-a", false)]
    [InlineData("octo-ai-adapter", null, false)]
    [InlineData("other-client", "OctoSystem", false)]
    public void IsTrustedPair_OnlyTheExactClientOfTheSystemTenant(string clientId, string? tenantId, bool expected)
    {
        AiServiceClientTrust.IsTrustedPair(Service(clientId, tenantId), " octo-ai-adapter ", "OctoSystem")
            .Should().Be(expected);
    }

    [Fact]
    public void IsTrustedPair_UserPrincipalOrEmptySettings_IsFalse()
    {
        AiServiceClientTrust.IsTrustedPair(new McpCallerPrincipal("user-1", "octo-ai-adapter", "OctoSystem", []),
            "octo-ai-adapter", "OctoSystem").Should().BeFalse();
        AiServiceClientTrust.IsTrustedPair(Service("octo-ai-adapter", "OctoSystem"), "", "OctoSystem")
            .Should().BeFalse();
        AiServiceClientTrust.IsTrustedPair(Service("octo-ai-adapter", "OctoSystem"), "octo-ai-adapter", null)
            .Should().BeFalse();
    }
}
