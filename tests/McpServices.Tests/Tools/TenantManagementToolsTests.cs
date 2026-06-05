using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Tests for the tenant lifecycle MCP tools (get/create/delete/clean/attach/detach/clear-cache/update-system-ck).
/// </summary>
public class TenantManagementToolsTests : ToolTestBase
{
    public TenantManagementToolsTests()
    {
        GivenAuthenticated();
    }

    // ----- get_tenants -----

    [Fact]
    public async Task GetTenants_HappyPath_ReturnsList()
    {
        MockAssetClient
            .Setup(c => c.GetTenantsAsync())
            .ReturnsAsync(new[]
            {
                new TenantDto { TenantId = "t1", Database = "db1" },
                new TenantDto { TenantId = "t2", Database = "db2" }
            });

        var result = await TenantManagementTools.GetTenants(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Tenants.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.ParentTenantId.Should().Be(DefaultTenantId);
    }

    [Fact]
    public async Task GetTenants_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await TenantManagementTools.GetTenants(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task GetTenants_EmptyList_StillSucceeds()
    {
        MockAssetClient.Setup(c => c.GetTenantsAsync()).ReturnsAsync([]);

        var result = await TenantManagementTools.GetTenants(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Tenants.Should().BeEmpty();
        result.Message.Should().Contain("No child tenants");
    }

    // ----- create_tenant -----

    [Fact]
    public async Task CreateTenant_HappyPath_LowerCasesAndCalls()
    {
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, "NewTenant", "NewDb");

        result.IsSuccess.Should().BeTrue();
        result.CreatedTenantId.Should().Be("newtenant");
        result.Database.Should().Be("newdb");
        MockAssetClient.Verify(c => c.CreateTenantAsync("newtenant", "newdb"), Times.Once);
    }

    [Fact]
    public async Task CreateTenant_MissingArgs_ReturnsValidationError()
    {
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, "", "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ----- delete_tenant -----

    [Fact]
    public async Task DeleteTenant_WithoutConfirm_Refuses()
    {
        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, "doomed", confirm: false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockAssetClient.Verify(c => c.DeleteTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTenant_WithConfirm_CallsSdk()
    {
        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, "doomed", confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.DeletedTenantId.Should().Be("doomed");
        MockAssetClient.Verify(c => c.DeleteTenantAsync("doomed"), Times.Once);
    }

    // ----- clean_tenant -----

    [Fact]
    public async Task CleanTenant_WithoutConfirm_Refuses()
    {
        var result = await TenantManagementTools.CleanTenant(MockServer.Object, "t1");

        result.IsSuccess.Should().BeFalse();
        MockAssetClient.Verify(c => c.CleanTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CleanTenant_WithConfirm_CallsSdk()
    {
        var result = await TenantManagementTools.CleanTenant(MockServer.Object, "t1", confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.ChildTenantId.Should().Be("t1");
        MockAssetClient.Verify(c => c.CleanTenantAsync("t1"), Times.Once);
    }

    // ----- attach_tenant -----

    [Fact]
    public async Task AttachTenant_HappyPath_CallsSdkLowerCased()
    {
        var result = await TenantManagementTools.AttachTenant(MockServer.Object, "NewT", "ExistingDb");

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.AttachTenantAsync("newt", "existingdb"), Times.Once);
    }

    [Fact]
    public async Task AttachTenant_MissingDatabase_ReturnsValidationError()
    {
        var result = await TenantManagementTools.AttachTenant(MockServer.Object, "t1", "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
        MockAssetClient.Verify(c => c.AttachTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ----- detach_tenant -----

    [Fact]
    public async Task DetachTenant_HappyPath_CallsSdk()
    {
        var result = await TenantManagementTools.DetachTenant(MockServer.Object, "t1");

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.DetachTenantAsync("t1"), Times.Once);
    }

    // ----- clear_tenant_cache -----

    [Fact]
    public async Task ClearTenantCache_WithoutConfirm_Refuses()
    {
        var result = await TenantManagementTools.ClearTenantCache(MockServer.Object, "t1");

        result.IsSuccess.Should().BeFalse();
        MockAssetClient.Verify(c => c.ClearTenantCacheAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClearTenantCache_WithConfirm_CallsSdk()
    {
        var result = await TenantManagementTools.ClearTenantCache(MockServer.Object, "t1", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.ClearTenantCacheAsync("t1"), Times.Once);
    }

    // ----- update_system_ck_model -----

    [Fact]
    public async Task UpdateSystemCkModel_HappyPath_CallsSdk()
    {
        var result = await TenantManagementTools.UpdateSystemCkModel(MockServer.Object, "t1");

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.UpdateSystemCkModelOfTenant("t1"), Times.Once);
    }

    // ----- SDK-exception propagation -----

    [Fact]
    public async Task GetTenants_WhenSdkThrows_ReturnsErrorMessage()
    {
        MockAssetClient
            .Setup(c => c.GetTenantsAsync())
            .ThrowsAsync(new InvalidOperationException("backend down"));

        var result = await TenantManagementTools.GetTenants(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("backend down");
    }
}
