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
        var result = await GroupManagementTools.CreateGroup(MockServer.Object,
            groupName: "G1", description: "desc", roleIds: ["r1", "r2"]);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.CreateGroup(It.Is<CreateGroupDto>(g =>
            g.GroupName == "G1" && g.GroupDescription == "desc" &&
            g.RoleIds!.Contains("r1") && g.RoleIds.Contains("r2"))), Times.Once);
    }

    [Fact]
    public async Task UpdateGroup_HappyPath_PassesUpdateDto()
    {
        var result = await GroupManagementTools.UpdateGroup(MockServer.Object,
            groupId: GroupId, groupName: "NewName", description: "new desc");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateGroup(
            It.Is<OctoObjectId>(o => o.ToString() == GroupId),
            It.Is<UpdateGroupDto>(g => g.GroupName == "NewName" && g.GroupDescription == "new desc")), Times.Once);
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
        var result = await GroupManagementTools.DeleteGroup(MockServer.Object, GroupId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteGroup(
            It.Is<OctoObjectId>(o => o.ToString() == GroupId)), Times.Once);
    }

    [Fact]
    public async Task UpdateGroupRoles_HappyPath_PassesList()
    {
        var result = await GroupManagementTools.UpdateGroupRoles(MockServer.Object,
            GroupId, ["r1", "r2"]);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateGroupRoles(
            It.Is<OctoObjectId>(o => o.ToString() == GroupId),
            It.Is<List<string>>(l => l.Count == 2 && l[0] == "r1")), Times.Once);
    }

    [Fact]
    public async Task UpdateGroupRoles_NullRoleIds_PassesEmptyList()
    {
        var result = await GroupManagementTools.UpdateGroupRoles(MockServer.Object, GroupId, null!);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateGroupRoles(
            It.IsAny<OctoObjectId>(),
            It.Is<List<string>>(l => l.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task AddUserToGroup_HappyPath_CallsSdk()
    {
        var result = await GroupManagementTools.AddUserToGroup(MockServer.Object, GroupId, UserId);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.AddUserToGroup(
            It.Is<OctoObjectId>(o => o.ToString() == GroupId), UserId), Times.Once);
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
        var result = await GroupManagementTools.RemoveUserFromGroup(MockServer.Object,
            GroupId, UserId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.RemoveUserFromGroup(
            It.Is<OctoObjectId>(o => o.ToString() == GroupId), UserId), Times.Once);
    }

    [Fact]
    public async Task AddGroupToGroup_HappyPath_CallsSdk()
    {
        var result = await GroupManagementTools.AddGroupToGroup(MockServer.Object,
            ParentGroupId, ChildGroupId);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.AddGroupToGroup(
            It.Is<OctoObjectId>(o => o.ToString() == ParentGroupId), ChildGroupId), Times.Once);
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
        var result = await GroupManagementTools.RemoveGroupFromGroup(MockServer.Object,
            ParentGroupId, ChildGroupId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.RemoveGroupFromGroup(
            It.Is<OctoObjectId>(o => o.ToString() == ParentGroupId), ChildGroupId), Times.Once);
    }
}
