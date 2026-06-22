using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for <see cref="McpSessionContext" />.TryGetAccessTokenAsync — the central resolver every
///     SDK-backed tool uses to find the OAuth bearer for downstream calls.
///     <para>
///     Covers the three sources in priority order: (1) fresh session-store token, (2) expired
///     session-store token with refresh-token grant via <see cref="ISessionTokenRefresher" />,
///     (3) HTTP <c>Authorization: Bearer</c> header fallback used by the headless OctoMesh AI
///     worker pod.
///     </para>
/// </summary>
public class McpSessionContextTests
{
    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore = new();
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor = new();
    private readonly Mock<ISessionTokenRefresher> _mockRefresher = new();
    private readonly Mock<McpServer> _mockServer = new();
    private readonly ServiceProvider _services;

    public McpSessionContextTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockTokenStore.Object);
        services.AddSingleton(_mockHttpContextAccessor.Object);
        services.AddSingleton(_mockRefresher.Object);
        _services = services.BuildServiceProvider();
        _mockServer.Setup(s => s.Services).Returns(_services);
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenSessionStoreHasFreshToken_ReturnsThatToken()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "session-store-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("session-store-token",
            "the session store is the primary source — device-flow login path");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "fresh tokens must NOT trigger a refresh");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreEmpty_FallsBackToHttpBearerHeader()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "the HTTP-layer Bearer is the fallback the AI worker pod relies on");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "no stored refresh token → no refresh attempt");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreExpiredAndNoRefreshToken_FallsBackToHttpBearer()
    {
        // Expired stored token but no refresh token (e.g., legacy session) — must NOT try to refresh,
        // fall through to the HTTP-bearer fallback.
        var sessionId = TestSessionId();
        _mockTokenStore.Setup(s => s.GetTokens(sessionId))
            .Returns(new McpSessionTokens
            {
                AccessToken = "stale-store-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
                RefreshToken = null
            });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "no refresh token in store → no refresh attempt");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenExpiredWithRefreshToken_RefreshesAndStoresNewTokens()
    {
        var sessionId = TestSessionId();
        _mockTokenStore.Setup(s => s.GetTokens(sessionId))
            .Returns(new McpSessionTokens
            {
                AccessToken = "expired-access",
                RefreshToken = "valid-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });

        var refreshed = new McpSessionTokens
        {
            AccessToken = "fresh-access",
            RefreshToken = "rotated-refresh",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        _mockRefresher.Setup(r => r.RefreshAsync("valid-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("fresh-access",
            "an expired session token with a refresh token must trigger a refresh");
        _mockTokenStore.Verify(s => s.SetTokens(sessionId, refreshed), Times.Once,
            "the refreshed tokens must be written back to the store");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenRefreshFails_RemovesTokensAndFallsBackToBearer()
    {
        var sessionId = TestSessionId();
        _mockTokenStore.Setup(s => s.GetTokens(sessionId))
            .Returns(new McpSessionTokens
            {
                AccessToken = "expired-access",
                RefreshToken = "revoked-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });

        _mockRefresher.Setup(r => r.RefreshAsync("revoked-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "refresh failure must drop the session token and fall through to the HTTP bearer");
        _mockTokenStore.Verify(s => s.RemoveTokens(sessionId), Times.Once,
            "stale tokens must be removed so subsequent calls don't keep retrying with the same bad refresh");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenRefreshFailsAndNoBearer_ReturnsNull()
    {
        var sessionId = TestSessionId();
        _mockTokenStore.Setup(s => s.GetTokens(sessionId))
            .Returns(new McpSessionTokens
            {
                AccessToken = "expired-access",
                RefreshToken = "revoked-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });
        _mockRefresher.Setup(r => r.RefreshAsync("revoked-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(new DefaultHttpContext());

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("refresh failed, no header bearer → caller is no longer authenticated");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreEmptyAndNoBearerHeader_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(new DefaultHttpContext());

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("no session-store token, no HTTP bearer → unauthenticated");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenHeaderHasNonBearerScheme_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("only Bearer scheme is forwarded to downstream Octo API clients");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_BearerHeaderIsCaseInsensitive()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "bearer mixed-case-scheme";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("mixed-case-scheme",
            "RFC 6750 §2.1 — the scheme is case-insensitive; misreading lowercase 'bearer' would mute a valid request");
    }

    /// <summary>
    ///     Derive the same session id key the implementation uses, so the mock setups on
    ///     specific session ids line up regardless of test-run order or McpServer fallbacks.
    /// </summary>
    private string TestSessionId() => McpSessionContext.GetSessionId(_mockServer.Object);
}
