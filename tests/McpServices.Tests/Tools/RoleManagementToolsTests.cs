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
        CkMethods.Succeeds(RoleRtId);

        var result = await RoleManagementTools.CreateRole(MockServer.Object, "Auditors");

        result.IsSuccess.Should().BeTrue();
        result.RoleName.Should().Be("Auditors");
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityRoleCreateRole"), "name").Should().Be("Auditors");
        MockIdentityClient.Verify(c => c.CreateRole(It.IsAny<RoleDto>()), Times.Never);
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
        MockIdentityClient.Setup(c => c.GetRoleByName("Old")).ReturnsAsync(new RoleDto { Id = RoleRtId, Name = "Old" });
        CkMethods.Succeeds();

        var result = await RoleManagementTools.UpdateRole(MockServer.Object, "Old", "New");

        result.IsSuccess.Should().BeTrue();
        result.RoleName.Should().Be("New");
        CkMethods.RtIdOf("SystemIdentityRoleUpdateRole").Should().Be(RoleRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityRoleUpdateRole"), "name").Should().Be("New");
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
        MockIdentityClient.Setup(c => c.GetRoleByName("Auditors"))
            .ReturnsAsync(new RoleDto { Id = RoleRtId, Name = "Auditors" });
        CkMethods.Succeeds();

        var result = await RoleManagementTools.DeleteRole(MockServer.Object, "Auditors", confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityRoleDeleteRole").Should().Be(RoleRtId);
        MockIdentityClient.Verify(c => c.DeleteRole(It.IsAny<string>()), Times.Never);
    }

    private const string RoleRtId = "507f1f77bcf86cd799439099";
}
