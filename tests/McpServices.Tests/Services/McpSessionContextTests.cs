using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for <see cref="McpSessionContext" />.TryGetAccessToken — the central resolver every
///     SDK-backed tool uses to find the OAuth bearer for downstream calls. The fallback path
///     (HTTP <c>Authorization: Bearer</c> when the session store is empty) is what makes the
///     headless OctoMesh AI worker pod work: its <c>.mcp.json</c> carries a Bearer header but
///     never calls the device-flow <c>authenticate</c> tool, so without the fallback every
///     BlueprintTool / IdentityTool / etc. would refuse with "Not authenticated."
/// </summary>
public class McpSessionContextTests
{
    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore = new();
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor = new();
    private readonly Mock<McpServer> _mockServer = new();
    private readonly ServiceProvider _services;

    public McpSessionContextTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockTokenStore.Object);
        services.AddSingleton(_mockHttpContextAccessor.Object);
        _services = services.BuildServiceProvider();
        _mockServer.Setup(s => s.Services).Returns(_services);
    }

    [Fact]
    public void TryGetAccessToken_WhenSessionStoreHasFreshToken_ReturnsThatToken()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "session-store-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().Be("session-store-token",
            "the session store is the primary source — device-flow login path");
    }

    [Fact]
    public void TryGetAccessToken_WhenStoreEmpty_FallsBackToHttpBearerHeader()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "the HTTP-layer Bearer is the fallback the AI worker pod relies on");
    }

    [Fact]
    public void TryGetAccessToken_WhenStoreExpiredAndHeaderPresent_FallsBackToHttpBearer()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "stale-store-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)   // expired
            });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "an expired stored token must not shadow a live HTTP bearer");
    }

    [Fact]
    public void TryGetAccessToken_WhenStoreEmptyAndNoBearerHeader_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(new DefaultHttpContext());

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().BeNull("no session-store token, no HTTP bearer → unauthenticated");
    }

    [Fact]
    public void TryGetAccessToken_WhenHeaderHasNonBearerScheme_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";   // not Bearer
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().BeNull("only Bearer scheme is forwarded to downstream Octo API clients");
    }

    [Fact]
    public void TryGetAccessToken_BearerHeaderIsCaseInsensitive()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "bearer mixed-case-scheme";
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(httpContext);

        var token = McpSessionContext.TryGetAccessToken(_mockServer.Object);

        token.Should().Be("mixed-case-scheme",
            "RFC 6750 §2.1 — the scheme is case-insensitive; misreading lowercase 'bearer' would mute a valid request");
    }
}
