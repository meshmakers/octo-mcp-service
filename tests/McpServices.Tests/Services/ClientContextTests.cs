using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for <see cref="IdentityClientContext"/> and <see cref="AssetClientContext"/> — the shared
///     bootstrapping helpers used by every tool that talks to the Identity / Asset service.
/// </summary>
public class ClientContextTests : ToolTestBase
{
    public ClientContextTests()
    {
        // ToolTestBase wires MockClientFactory + MockTokenStore via the TestServiceProvider.
        // We default to unauthenticated so individual tests have to opt in.
        GivenUnauthenticated();
    }

    [Fact]
    public async Task IdentityClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.TenantId.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task IdentityClientContext_WhenAuthenticated_ReturnsClient()
    {
        GivenAuthenticated("tok-1");

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        ctx.Error.Should().BeNull();

        MockClientFactory.Verify(f => f.CreateIdentityClient(DefaultTenantId, "tok-1"), Times.Once);
    }

    [Fact]
    public async Task IdentityClientContext_WithExplicitTenant_PassesItToResolver()
    {
        GivenAuthenticated();
        MockTenantResolution.Setup(t => t.ResolveTenantId("explicit-tenant")).Returns("explicit-tenant");

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, "explicit-tenant");

        ctx.TenantId.Should().Be("explicit-tenant");
        MockClientFactory.Verify(f => f.CreateIdentityClient("explicit-tenant", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IdentityClientContext_WithExpiredToken_ReturnsAuthError()
    {
        GivenTokenExpired();

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task IdentityClientContext_WhenResolverThrows_PropagatesAsError()
    {
        GivenAuthenticated();
        MockTenantResolution
            .Setup(t => t.ResolveTenantId(It.IsAny<string?>()))
            .Throws(new InvalidOperationException("No tenant ID specified."));

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Be("No tenant ID specified.");
    }

    [Fact]
    public async Task AssetClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task AssetClientContext_WhenAuthenticated_ReturnsClient()
    {
        GivenAuthenticated("tok-2");

        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateAssetClient(DefaultTenantId, "tok-2"), Times.Once);
    }

    [Fact]
    public async Task CommunicationClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await CommunicationClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task CommunicationClientContext_WhenAuthenticated_ReturnsClient()
    {
        GivenAuthenticated("tok-3");

        var ctx = await CommunicationClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateCommunicationClient(DefaultTenantId, "tok-3"), Times.Once);
    }

    [Fact]
    public async Task StreamDataClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await StreamDataClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task StreamDataClientContext_WhenAuthenticated_ReturnsClient()
    {
        GivenAuthenticated("tok-sd");

        var ctx = await StreamDataClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateStreamDataClient(DefaultTenantId, "tok-sd"), Times.Once);
    }

    [Fact]
    public async Task ReportingClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await ReportingClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task ReportingClientContext_WhenAuthenticated_ReturnsClient()
    {
        GivenAuthenticated("tok-rep");

        var ctx = await ReportingClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateReportingClient(DefaultTenantId, "tok-rep"), Times.Once);
    }
}
