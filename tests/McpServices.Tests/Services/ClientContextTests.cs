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
        var token = GivenAuthenticated();

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        ctx.Error.Should().BeNull();

        MockClientFactory.Verify(f => f.CreateIdentityClient(DefaultTenantId, token), Times.Once);
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
        var token = GivenAuthenticated();

        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateAssetClient(DefaultTenantId, token), Times.Once);
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
        var token = GivenAuthenticated();

        var ctx = await CommunicationClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateCommunicationClient(DefaultTenantId, token), Times.Once);
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
        var token = GivenAuthenticated();

        var ctx = await StreamDataClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateStreamDataClient(DefaultTenantId, token), Times.Once);
    }

    [Fact]
    public async Task ReportingClientContext_WhenUnauthenticated_ReturnsAuthError()
    {
        var ctx = await ReportingClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("Not authenticated");
    }

    // ── AB#5036: the session store is bound to the authenticated caller ─────────────────────────

    [Fact]
    public async Task ClientContext_WhenStoreTokenBelongsToAnotherPrincipal_RefusesWithItsOwnMessage()
    {
        // The family-1 half of AB#5036: every *ClientContext presents the stored token to a backend
        // service, so this is where a borrowed token would have turned into a foreign identity.
        GivenAuthenticated(TestJwt.CreateFull(DefaultTenantId, "somebody-else", clientId: null, "Admin"));

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("does not belong to the authenticated caller");
        ctx.Error.Should().NotStartWith("Not authenticated",
            "the caller IS authenticated — saying otherwise would trigger a pointless device-flow re-login");
        MockClientFactory.Verify(f => f.CreateIdentityClient(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never, "no SDK client may be built with a token that is not the caller's");
    }

    [Fact]
    public async Task ClientContext_WhenStoreTokenIsForAnotherTenant_RefusesWithItsOwnMessage()
    {
        GivenAuthenticated(TestJwt.CreateFull("some-other-tenant", DefaultCallerSubjectId, clientId: null));

        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().BeNull();
        ctx.Error.Should().Contain("does not belong to the authenticated caller");
    }

    [Fact]
    public async Task ClientContext_ForServicePrincipal_KeepsUsingTheBearerHeader()
    {
        // Regression guard for the AI Adapter worker and the mesh-adapter: no store entry, opaque token in
        // the Authorization header, no `sub` anywhere. This path must be untouched by AB#5036 — breaking
        // it costs both components access to every tenant they serve.
        GivenServicePrincipalCaller();
        GivenUnauthenticated();
        TestHttpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";

        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Error.Should().BeNull();
        ctx.Client.Should().NotBeNull();
        MockClientFactory.Verify(f => f.CreateAssetClient(DefaultTenantId, "adapter-minted-bearer-xyz"),
            Times.Once);
    }

    [Fact]
    public async Task ClientContext_ForServicePrincipal_MayStillUseAnOpaqueStoreToken()
    {
        // A client-credentials token has no `sub`, so the binding check cannot be asked of it and is
        // deliberately skipped (AB#5030 / AB#5032 exemption). Its store namespace is its client_id.
        GivenServicePrincipalCaller();
        GivenAuthenticated("an-opaque-service-token");

        var ctx = await AssetClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Error.Should().BeNull();
        MockClientFactory.Verify(f => f.CreateAssetClient(DefaultTenantId, "an-opaque-service-token"),
            Times.Once);
    }

    [Fact]
    public async Task ClientContext_CrossTenantExchangeStillWorks()
    {
        // AB#4338 must survive the binding: the home token binds to the caller, the exchange produces the
        // B token, and the B token is what reaches the SDK client.
        var homeToken = GivenAuthenticated();
        MockTenantResolution.Setup(t => t.ResolveTenantId("tenant-b")).Returns("tenant-b");
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(homeToken, "tenant-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSessionTokens
            {
                AccessToken = "b-scoped-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var ctx = await IdentityClientContext.TryBuildAsync(MockServer.Object, "tenant-b");

        ctx.Error.Should().BeNull();
        ctx.TenantId.Should().Be("tenant-b");
        MockClientFactory.Verify(f => f.CreateIdentityClient("tenant-b", "b-scoped-token"), Times.Once);
    }

    [Fact]
    public async Task ReportingClientContext_WhenAuthenticated_ReturnsClient()
    {
        var token = GivenAuthenticated();

        var ctx = await ReportingClientContext.TryBuildAsync(MockServer.Object, tenantIdParam: null);

        ctx.Client.Should().NotBeNull();
        ctx.TenantId.Should().Be(DefaultTenantId);
        MockClientFactory.Verify(f => f.CreateReportingClient(DefaultTenantId, token), Times.Once);
    }
}
