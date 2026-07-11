using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for the in-memory MCP session token store.
/// </summary>
public class McpSessionTokenStoreTests
{
    private readonly McpSessionTokenStore _store = new();

    [Fact]
    public void SetTokens_And_GetTokens_ReturnsStoredTokens()
    {
        // Arrange
        var tokens = new McpSessionTokens
        {
            AccessToken = "test-access-token",
            RefreshToken = "test-refresh-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        // Act
        _store.SetTokens("session-1", tokens);
        var result = _store.GetTokens("session-1");

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("test-access-token");
        result.RefreshToken.Should().Be("test-refresh-token");
    }

    [Fact]
    public void GetTokens_WithUnknownSession_ReturnsNull()
    {
        var result = _store.GetTokens("unknown-session");
        result.Should().BeNull();
    }

    [Fact]
    public void RemoveTokens_RemovesStoredTokens()
    {
        // Arrange
        _store.SetTokens("session-1", new McpSessionTokens
        {
            AccessToken = "token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });

        // Act
        _store.RemoveTokens("session-1");

        // Assert
        _store.GetTokens("session-1").Should().BeNull();
    }

    [Fact]
    public void IsExpired_WhenTokenExpired_ReturnsTrue()
    {
        var tokens = new McpSessionTokens
        {
            AccessToken = "expired-token",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        tokens.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WhenTokenValid_ReturnsFalse()
    {
        var tokens = new McpSessionTokens
        {
            AccessToken = "valid-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        tokens.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void SessionIsolation_DifferentSessions_DoNotInterfere()
    {
        // Arrange
        _store.SetTokens("session-a", new McpSessionTokens
        {
            AccessToken = "token-a",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        _store.SetTokens("session-b", new McpSessionTokens
        {
            AccessToken = "token-b",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });

        // Act & Assert
        _store.GetTokens("session-a")!.AccessToken.Should().Be("token-a");
        _store.GetTokens("session-b")!.AccessToken.Should().Be("token-b");

        // Remove one session
        _store.RemoveTokens("session-a");
        _store.GetTokens("session-a").Should().BeNull();
        _store.GetTokens("session-b").Should().NotBeNull();
    }

    [Fact]
    public void TenantTokens_SetAndGet_KeyedBySessionAndTenant()
    {
        var tokens = new McpSessionTokens
        {
            AccessToken = "b-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        _store.SetTenantTokens("session-1", "tenant-b", tokens);

        _store.GetTenantTokens("session-1", "tenant-b")!.AccessToken.Should().Be("b-token");
        _store.GetTenantTokens("session-1", "tenant-c").Should().BeNull("different tenant → different cache entry");
        _store.GetTenantTokens("session-2", "tenant-b").Should().BeNull("different session → different cache entry");
    }

    [Fact]
    public void TenantTokens_DoNotCollideWithHomeToken()
    {
        _store.SetTokens("session-1", new McpSessionTokens
        {
            AccessToken = "home-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        _store.SetTenantTokens("session-1", "tenant-b", new McpSessionTokens
        {
            AccessToken = "b-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });

        _store.GetTokens("session-1")!.AccessToken.Should().Be("home-token");
        _store.GetTenantTokens("session-1", "tenant-b")!.AccessToken.Should().Be("b-token");
    }

    [Fact]
    public void RemoveTenantTokens_RemovesOnlyThatEntry()
    {
        _store.SetTenantTokens("session-1", "tenant-b", new McpSessionTokens
        {
            AccessToken = "b-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        _store.SetTenantTokens("session-1", "tenant-c", new McpSessionTokens
        {
            AccessToken = "c-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });

        _store.RemoveTenantTokens("session-1", "tenant-b");

        _store.GetTenantTokens("session-1", "tenant-b").Should().BeNull();
        _store.GetTenantTokens("session-1", "tenant-c").Should().NotBeNull();
    }

    [Fact]
    public void DeviceAuthorization_SetAndGet_Works()
    {
        // Arrange
        var state = new DeviceAuthorizationState
        {
            DeviceCode = "device-code-123",
            UserCode = "ABCD-1234",
            VerificationUri = "https://identity.example.com/device",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            IntervalSeconds = 5
        };

        // Act
        _store.SetDeviceAuthorization("session-1", state);
        var result = _store.GetDeviceAuthorization("session-1");

        // Assert
        result.Should().NotBeNull();
        result!.DeviceCode.Should().Be("device-code-123");
        result.UserCode.Should().Be("ABCD-1234");
    }

    [Fact]
    public void DeviceAuthorization_RemoveAndGet_ReturnsNull()
    {
        // Arrange
        _store.SetDeviceAuthorization("session-1", new DeviceAuthorizationState
        {
            DeviceCode = "code",
            UserCode = "ABCD",
            VerificationUri = "https://example.com",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            IntervalSeconds = 5
        });

        // Act
        _store.RemoveDeviceAuthorization("session-1");

        // Assert
        _store.GetDeviceAuthorization("session-1").Should().BeNull();
    }
}
