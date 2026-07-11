using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for the tenant-aware <see cref="McpSessionContext" />.TryGetAccessTokenAsync overload
///     (AB#4338): home tenant → home token (no exchange); target tenant absent → exchange invoked once
///     and cached; second call → cache hit (no second exchange).
/// </summary>
public class McpSessionContextTenantTests
{
    private const string TargetTenant = "tenant-b";

    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore = new();
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor = new();
    private readonly Mock<ISessionTokenRefresher> _mockRefresher = new();
    private readonly Mock<ITenantTokenExchanger> _mockExchanger = new();
    private readonly Mock<McpServer> _mockServer = new();
    private readonly ServiceProvider _services;

    public McpSessionContextTenantTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockTokenStore.Object);
        services.AddSingleton(_mockHttpContextAccessor.Object);
        services.AddSingleton(_mockRefresher.Object);
        services.AddSingleton(_mockExchanger.Object);
        _services = services.BuildServiceProvider();
        _mockServer.Setup(s => s.Services).Returns(_services);
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenTenantMatchesHome_ReturnsHomeTokenWithoutExchange()
    {
        var homeToken = TestJwt.Create("tenant-a");
        GivenHomeToken(homeToken);

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, "tenant-a", CancellationToken.None);

        token.Should().Be(homeToken, "the requested tenant equals the home token's tenant_id → no exchange");
        _mockExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenTenantNull_ReturnsHomeTokenWithoutExchange()
    {
        var homeToken = TestJwt.Create("tenant-a");
        GivenHomeToken(homeToken);

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, tenantId: null, CancellationToken.None);

        token.Should().Be(homeToken);
        _mockExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenTargetTenantAbsent_ExchangesOnceAndCaches()
    {
        var homeToken = TestJwt.Create("tenant-a");
        GivenHomeToken(homeToken);
        _mockTokenStore.Setup(s => s.GetTenantTokens(It.IsAny<string>(), TargetTenant))
            .Returns((McpSessionTokens?)null);

        var exchanged = new McpSessionTokens
        {
            AccessToken = "B-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        _mockExchanger.Setup(e => e.ExchangeForTenantAsync(homeToken, TargetTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exchanged);

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, TargetTenant, CancellationToken.None);

        token.Should().Be("B-token", "a different tenant with no cached token triggers a transparent exchange");
        _mockExchanger.Verify(
            e => e.ExchangeForTenantAsync(homeToken, TargetTenant, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockTokenStore.Verify(
            s => s.SetTenantTokens(It.IsAny<string>(), TargetTenant, exchanged),
            Times.Once, "the exchanged B token must be cached");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenTargetTokenCachedAndFresh_ReturnsCacheHitWithoutExchange()
    {
        var homeToken = TestJwt.Create("tenant-a");
        GivenHomeToken(homeToken);
        _mockTokenStore.Setup(s => s.GetTenantTokens(It.IsAny<string>(), TargetTenant))
            .Returns(new McpSessionTokens
            {
                AccessToken = "cached-B-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, TargetTenant, CancellationToken.None);

        token.Should().Be("cached-B-token");
        _mockExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "a fresh cached B token must be reused without a second exchange");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenExchangeFails_ReturnsNull()
    {
        var homeToken = TestJwt.Create("tenant-a");
        GivenHomeToken(homeToken);
        _mockTokenStore.Setup(s => s.GetTenantTokens(It.IsAny<string>(), TargetTenant))
            .Returns((McpSessionTokens?)null);
        _mockExchanger.Setup(e => e.ExchangeForTenantAsync(homeToken, TargetTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, TargetTenant, CancellationToken.None);

        token.Should().BeNull("a failed exchange surfaces as null so the tool reports an actionable error");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenNoHomeToken_ReturnsNullWithoutExchange()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(new DefaultHttpContext());

        var token = await McpSessionContext.TryGetAccessTokenAsync(
            _mockServer.Object, TargetTenant, CancellationToken.None);

        token.Should().BeNull("no home token → not authenticated → no exchange attempt");
        _mockExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void GivenHomeToken(string accessToken)
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = accessToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
    }
}
