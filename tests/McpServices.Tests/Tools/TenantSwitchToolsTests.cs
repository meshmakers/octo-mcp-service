using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Tests for <see cref="TenantSwitchTools" />.SwitchTenant (AB#4338): happy path (exchange + cache +
///     roles from the B token JWT), unauthenticated, missing tenantId, and exchange-failure — the last of
///     which must recommend the <c>authenticate</c> fallback.
/// </summary>
public class TenantSwitchToolsTests : ToolTestBase
{
    private const string TargetTenant = "tenant-b";

    [Fact]
    public async Task SwitchTenant_HappyPath_ExchangesCachesAndReturnsRoles()
    {
        GivenAuthenticated("home-token");
        var bToken = TestJwt.Create(TargetTenant, "Reader", "Writer");
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync("home-token", TargetTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSessionTokens
            {
                AccessToken = bToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var result = await TenantSwitchTools.SwitchTenant(MockServer.Object, TargetTenant);

        result.IsSuccess.Should().BeTrue();
        result.TenantId.Should().Be(TargetTenant);
        result.Roles.Should().BeEquivalentTo("Reader", "Writer");
        MockTokenStore.Verify(
            s => s.SetTenantTokens(It.IsAny<string>(), TargetTenant, It.IsAny<McpSessionTokens>()),
            Times.Once, "the exchanged B token must be cached");
    }

    [Fact]
    public async Task SwitchTenant_Unauthenticated_ReturnsAuthErrorWithoutExchange()
    {
        GivenUnauthenticated();

        var result = await TenantSwitchTools.SwitchTenant(MockServer.Object, TargetTenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockTokenExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SwitchTenant_MissingTenantId_ReturnsValidationErrorWithoutExchange()
    {
        GivenAuthenticated();

        var result = await TenantSwitchTools.SwitchTenant(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("tenantId");
        MockTokenExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SwitchTenant_WhenExchangeFails_RecommendsAuthenticate()
    {
        GivenAuthenticated("home-token");
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync("home-token", TargetTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        var result = await TenantSwitchTools.SwitchTenant(MockServer.Object, TargetTenant);

        result.IsSuccess.Should().BeFalse();
        result.TenantId.Should().Be(TargetTenant);
        result.ErrorMessage.Should().Contain("authenticate",
            "a failed exchange must recommend logging in against the target tenant directly");
        MockTokenStore.Verify(
            s => s.SetTenantTokens(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSessionTokens>()),
            Times.Never, "nothing to cache when the exchange fails");
    }

    [Fact]
    public async Task SwitchTenant_WhenAlreadyOnTargetTenant_ReturnsRolesWithoutExchange()
    {
        GivenAuthenticated(TestJwt.Create(TargetTenant, "Admin"));

        var result = await TenantSwitchTools.SwitchTenant(MockServer.Object, TargetTenant);

        result.IsSuccess.Should().BeTrue();
        result.TenantId.Should().Be(TargetTenant);
        result.Roles.Should().BeEquivalentTo("Admin");
        MockTokenExchanger.Verify(
            e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "already on the target tenant → no exchange");
    }
}
