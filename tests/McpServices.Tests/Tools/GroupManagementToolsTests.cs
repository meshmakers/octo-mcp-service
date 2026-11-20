using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class GroupManagementToolsTests : ToolTestBase
{
    // Valid 24-char hex string accepted by OctoObjectId(string) (MongoDB ObjectId format).
    private const string GroupId = "507f1f77bcf86cd799439011";
    private const string ParentGroupId = "507f1f77bcf86cd799439022";
    private const string ChildGroupId = "507f1f77bcf86cd799439033";
    private const string UserId = "user-123";

    public GroupManagementToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetGroups_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetGroups())
            .ReturnsAsync(new[] { new GroupDto { Id = new OctoObjectId(GroupId), GroupName = "G1" } });

        var result = await GroupManagementTools.GetGroups(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Groups.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetGroups_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await GroupManagementTools.GetGroups(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task GetGroup_HappyPath_PassesOctoObjectId()
    {
        MockIdentityClient.Setup(c => c.GetGroup(It.Is<OctoObjectId>(o => o.ToString() == GroupId)))
            .ReturnsAsync(new GroupDto { Id = new OctoObjectId(GroupId), GroupName = "G1" });

        var result = await GroupManagementTools.GetGroup(MockServer.Object, GroupId);

        result.IsSuccess.Should().BeTrue();
        result.GroupId.Should().Be(GroupId);
        result.Group!.GroupName.Should().Be("G1");
    }

    [Fact]
    public async Task GetGroup_MissingId_ReturnsValidationError()
    {
        var result = await GroupManagementTools.GetGroup(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.GetGroup(It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task CreateGroup_HappyPath_PassesCreateDto()
    {
        // Group.CreateGroup-1 takes role names: role ids of the tenant become names, other entries are names already.
        MockIdentityClient.Setup(c => c.GetRoles()).ReturnsAsync([new RoleDto { Id = "r1", Name = "Reader" }]);
        CkMethods.Succeeds(GroupId);

        var result = await GroupManagementTools.CreateGroup(MockServer.Object,
            groupName: "G1", description: "desc", roleIds: ["r1", "Writer"]);

        result.IsSuccess.Should().BeTrue();
        var input = CkMethods.InputOf("SystemIdentityGroupCreateGroup");
        CkMethodFake.Str(input, "groupName").Should().Be("G1");
        CkMethodFake.Str(input, "groupDescription").Should().Be("desc");
        CkMethodFake.Strings(input.GetProperty("roleNames")).Should().Equal("Reader", "Writer");
        MockIdentityClient.Verify(c => c.CreateGroup(It.IsAny<CreateGroupDto>()), Times.Never);
    }

    [Fact]
    public async Task UpdateGroup_HappyPath_PassesUpdateDto()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.UpdateGroup(MockServer.Object,
            groupId: GroupId, groupName: "NewName", description: "new desc");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupUpdateGroup").Should().Be(GroupId);
        var input = CkMethods.InputOf("SystemIdentityGroupUpdateGroup");
        CkMethodFake.Str(input, "groupName").Should().Be("NewName");
        CkMethodFake.Str(input, "groupDescription").Should().Be("new desc");
    }

    [Fact]
    public async Task DeleteGroup_WithoutConfirm_Refuses()
    {
        var result = await GroupManagementTools.DeleteGroup(MockServer.Object, GroupId);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteGroup(It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGroup_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.DeleteGroup(MockServer.Object, GroupId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupDeleteGroup").Should().Be(GroupId);
    }

    [Fact]
    public async Task UpdateGroupRoles_HappyPath_PassesList()
    {
        MockIdentityClient.Setup(c => c.GetRoles()).ReturnsAsync(
            [new RoleDto { Id = "r1", Name = "Reader" }, new RoleDto { Id = "r2", Name = "Writer" }]);
        CkMethods.Succeeds();

        var result = await GroupManagementTools.UpdateGroupRoles(MockServer.Object,
            GroupId, ["r1", "r2"]);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupSetRoles").Should().Be(GroupId);
        CkMethodFake.Strings(CkMethods.InputOf("SystemIdentityGroupSetRoles").GetProperty("roleNames"))
            .Should().Equal("Reader", "Writer");
    }

    [Fact]
    public async Task UpdateGroupRoles_NullRoleIds_PassesEmptyList()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.UpdateGroupRoles(MockServer.Object, GroupId, null!);

        result.IsSuccess.Should().BeTrue();
        CkMethodFake.Strings(CkMethods.InputOf("SystemIdentityGroupSetRoles").GetProperty("roleNames"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task AddUserToGroup_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.AddUserToGroup(MockServer.Object, GroupId, UserId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupAddMember").Should().Be(GroupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupAddMember"), "memberId").Should().Be(UserId);
    }

    [Fact]
    public async Task RemoveUserFromGroup_WithoutConfirm_Refuses()
    {
        var result = await GroupManagementTools.RemoveUserFromGroup(MockServer.Object, GroupId, UserId);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.RemoveUserFromGroup(It.IsAny<OctoObjectId>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveUserFromGroup_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.RemoveUserFromGroup(MockServer.Object,
            GroupId, UserId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupRemoveMember").Should().Be(GroupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupRemoveMember"), "memberId").Should().Be(UserId);
    }

    [Fact]
    public async Task AddGroupToGroup_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.AddGroupToGroup(MockServer.Object,
            ParentGroupId, ChildGroupId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupAddChildGroup").Should().Be(ParentGroupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupAddChildGroup"), "childGroupId").Should().Be(ChildGroupId);
    }

    [Fact]
    public async Task RemoveGroupFromGroup_WithoutConfirm_Refuses()
    {
        var result = await GroupManagementTools.RemoveGroupFromGroup(MockServer.Object,
            ParentGroupId, ChildGroupId);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.RemoveGroupFromGroup(It.IsAny<OctoObjectId>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveGroupFromGroup_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await GroupManagementTools.RemoveGroupFromGroup(MockServer.Object,
            ParentGroupId, ChildGroupId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupRemoveChildGroup").Should().Be(ParentGroupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupRemoveChildGroup"), "childGroupId")
            .Should().Be(ChildGroupId);
    }
}
