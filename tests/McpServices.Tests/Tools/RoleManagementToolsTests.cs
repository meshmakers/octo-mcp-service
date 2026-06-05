using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class RoleManagementToolsTests : ToolTestBase
{
    public RoleManagementToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetRoles_HappyPath_ReturnsList()
    {
        MockIdentityClient
            .Setup(c => c.GetRoles())
            .ReturnsAsync(new[]
            {
                new RoleDto { Name = "Admin" },
                new RoleDto { Name = "Viewer" }
            });

        var result = await RoleManagementTools.GetRoles(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Roles.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRoles_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await RoleManagementTools.GetRoles(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task CreateRole_HappyPath_CallsSdk()
    {
        var result = await RoleManagementTools.CreateRole(MockServer.Object, "Auditors");

        result.IsSuccess.Should().BeTrue();
        result.RoleName.Should().Be("Auditors");
        MockIdentityClient.Verify(c => c.CreateRole(It.Is<RoleDto>(r => r.Name == "Auditors")), Times.Once);
    }

    [Fact]
    public async Task CreateRole_MissingName_ReturnsValidationError()
    {
        var result = await RoleManagementTools.CreateRole(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.CreateRole(It.IsAny<RoleDto>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRole_HappyPath_CallsSdk()
    {
        var result = await RoleManagementTools.UpdateRole(MockServer.Object, "Old", "New");

        result.IsSuccess.Should().BeTrue();
        result.RoleName.Should().Be("New");
        MockIdentityClient.Verify(c => c.UpdateRole("Old", It.Is<RoleDto>(r => r.Name == "New")), Times.Once);
    }

    [Fact]
    public async Task DeleteRole_WithoutConfirm_Refuses()
    {
        var result = await RoleManagementTools.DeleteRole(MockServer.Object, "Auditors");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteRole(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRole_WithConfirm_CallsSdk()
    {
        var result = await RoleManagementTools.DeleteRole(MockServer.Object, "Auditors", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteRole("Auditors"), Times.Once);
    }
}
