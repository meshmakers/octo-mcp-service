using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Tests for the authentication tools.
/// </summary>
public class AuthenticationToolsTests : TestBase
{
    public AuthenticationToolsTests()
    {
        // Register authentication-related services
        var mockTokenStore = new Mock<IMcpSessionTokenStore>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockOptions = new Mock<IOptions<McpServiceOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new McpServiceOptions());

        TestServiceProvider.RegisterService(mockTokenStore.Object);
        TestServiceProvider.RegisterService(mockHttpClientFactory.Object);
        TestServiceProvider.RegisterService(mockOptions.Object);

        _mockTokenStore = mockTokenStore;
        _mockHttpClientFactory = mockHttpClientFactory;
    }

    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;

    [Fact]
    public async Task Authenticate_WhenAlreadyAuthenticated_ReturnsAlreadyAuthenticated()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "existing-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        // Act
        var result = await AuthenticationTools.Authenticate(MockServer.Object, "test-tenant");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsAlreadyAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAuthStatus_WithNoDeviceAuthorization_ReturnsError()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _mockTokenStore.Setup(s => s.GetDeviceAuthorization(It.IsAny<string>()))
            .Returns((DeviceAuthorizationState?)null);

        // Act
        var result = await AuthenticationTools.CheckAuthStatus(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsAuthenticated.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No pending authentication");
    }

    [Fact]
    public async Task CheckAuthStatus_WhenAlreadyAuthenticated_ReturnsSuccess()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "valid-token",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        // Act
        var result = await AuthenticationTools.CheckAuthStatus(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task AuthStatus_WhenSessionFreshAndJwt_ReturnsClaims()
    {
        // Hand-crafted JWT (HS256, signature ignored — JwtSecurityTokenHandler.ReadJwtToken does not verify).
        // Payload: { "sub": "user-123", "name": "Alice", "tenant": "acme" }
        const string jwt =
            "eyJhbGciOiJIUzI1NiJ9." +
            "eyJzdWIiOiJ1c2VyLTEyMyIsIm5hbWUiOiJBbGljZSIsInRlbmFudCI6ImFjbWUifQ." +
            "QqXBnz5HwR2QmVlS5cGSrhYxsxV3SfNCs8U5DTKxDBI";
        var expires = DateTime.UtcNow.AddHours(1);
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens { AccessToken = jwt, ExpiresAtUtc = expires });

        var result = await AuthenticationTools.AuthStatus(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.IsAuthenticated.Should().BeTrue();
        result.WasRefreshed.Should().BeFalse("fresh token doesn't trigger a refresh");
        result.SubjectId.Should().Be("user-123");
        result.UserName.Should().Be("Alice");
        result.TenantId.Should().Be("acme");
        result.ExpiresAtUtc.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AuthStatus_WhenUnauthenticated_ReturnsNotAuthenticated()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);

        var result = await AuthenticationTools.AuthStatus(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.IsAuthenticated.Should().BeFalse();
        result.Message.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task AuthStatus_WhenOpaqueToken_StillReportsAuthenticatedWithoutClaims()
    {
        // Non-JWT bearer (e.g., adapter-minted opaque token) — IsAuthenticated must still be true
        // and the claim fields must be null rather than raising.
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = "opaque-not-a-jwt",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var result = await AuthenticationTools.AuthStatus(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.IsAuthenticated.Should().BeTrue();
        result.SubjectId.Should().BeNull();
        result.UserName.Should().BeNull();
        result.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task CheckAuthStatus_WhenDeviceAuthExpired_ReturnsExpiredError()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _mockTokenStore.Setup(s => s.GetDeviceAuthorization(It.IsAny<string>()))
            .Returns(new DeviceAuthorizationState
            {
                DeviceCode = "expired-code",
                UserCode = "ABCD",
                VerificationUri = "https://example.com",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1), // Expired
                IntervalSeconds = 5
            });

        // Act
        var result = await AuthenticationTools.CheckAuthStatus(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("expired");
        _mockTokenStore.Verify(s => s.RemoveDeviceAuthorization(It.IsAny<string>()), Times.Once);
    }
}
