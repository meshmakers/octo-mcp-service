using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Tests for the identity tools (whoami, list_tenants).
/// </summary>
public class IdentityToolsTests : TestBase
{
    public IdentityToolsTests()
    {
        _mockTokenStore = new Mock<IMcpSessionTokenStore>();
        TestServiceProvider.RegisterService(_mockTokenStore.Object);
    }

    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore;

    private static string CreateTestJwt(Dictionary<string, string>? claims = null)
    {
        var securityKey = new SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("this-is-a-test-key-that-is-long-enough-for-hs256"));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claimsList = new List<Claim>
        {
            new("sub", "user-123"),
            new("preferred_username", "testuser"),
            new("email", "test@example.com"),
            new("tenant_id", "test-tenant"),
            new("allowed_tenants", "tenant-a tenant-b tenant-c"),
            new("role", "Admin"),
            new("scope", "openid profile email octo_api")
        };

        if (claims != null)
        {
            foreach (var claim in claims)
            {
                claimsList.Add(new Claim(claim.Key, claim.Value));
            }
        }

        var token = new JwtSecurityToken(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: claimsList,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task WhoAmI_WhenAuthenticated_ReturnsUserInfo()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = CreateTestJwt(),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        // Act
        var result = await IdentityTools.WhoAmI(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsAuthenticated.Should().BeTrue();
        result.UserId.Should().Be("user-123");
        result.UserName.Should().Be("testuser");
        result.Email.Should().Be("test@example.com");
        result.TenantId.Should().Be("test-tenant");
        result.AllowedTenants.Should().Contain("tenant-a");
        result.AllowedTenants.Should().Contain("tenant-b");
        result.AllowedTenants.Should().Contain("tenant-c");
        result.Roles.Should().Contain("Admin");
    }

    [Fact]
    public async Task WhoAmI_WhenNotAuthenticated_ReturnsError()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);

        // Act
        var result = await IdentityTools.WhoAmI(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsAuthenticated.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task ListTenants_WhenAuthenticated_ReturnsTenantList()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = CreateTestJwt(),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        // Act
        var result = await IdentityTools.ListTenants(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.AllowedTenants.Should().HaveCount(3);
        result.AllowedTenants.Should().Contain("tenant-a");
        result.CurrentTenantId.Should().Be("test-tenant");
        result.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task ListTenants_WhenNotAuthenticated_ReturnsError()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);

        // Act
        var result = await IdentityTools.ListTenants(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task WhoAmI_WithExpiredToken_ReturnsError()
    {
        // Arrange
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = CreateTestJwt(),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) // Expired
            });

        // Act
        var result = await IdentityTools.WhoAmI(MockServer.Object);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsAuthenticated.Should().BeFalse();
    }
}
