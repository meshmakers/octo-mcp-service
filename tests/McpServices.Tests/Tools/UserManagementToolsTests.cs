using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class UserManagementToolsTests : ToolTestBase
{
    public UserManagementToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetUsers_HappyPath_ReturnsList()
    {
        MockIdentityClient
            .Setup(c => c.GetUsers())
            .ReturnsAsync(new[]
            {
                new UserDto { Name = "alice", Email = "alice@example.com" },
                new UserDto { Name = "bob", Email = "bob@example.com" }
            });

        var result = await UserManagementTools.GetUsers(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Users.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetUsers_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await UserManagementTools.GetUsers(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task CreateUser_HappyPath_LowerCasesAndPostsRegisterDto()
    {
        var result = await UserManagementTools.CreateUser(MockServer.Object,
            userName: "Alice",
            email: "Alice@Example.com",
            password: "p@ssw0rd");

        result.IsSuccess.Should().BeTrue();
        result.User.Should().Be("alice");

        MockIdentityClient.Verify(c => c.CreateUser(It.Is<RegisterUserDto>(u =>
            u.Name == "alice" && u.Email == "alice@example.com" && u.Password == "p@ssw0rd")), Times.Once);
    }

    [Fact]
    public async Task CreateUser_MissingFields_ReturnsValidationError()
    {
        var result = await UserManagementTools.CreateUser(MockServer.Object, "", "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.CreateUser(It.IsAny<UserDto>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_NoChange_ReturnsValidationError()
    {
        var result = await UserManagementTools.UpdateUser(MockServer.Object, "alice");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("newEmail or newUserName");
    }

    [Fact]
    public async Task UpdateUser_HappyPath_PatchesEmailAndName()
    {
        var result = await UserManagementTools.UpdateUser(MockServer.Object,
            userNameOrEmail: "Alice",
            newEmail: "NEW@Example.com",
            newUserName: "Alice2");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateUser("alice", It.Is<UserDto>(u =>
            u.Email == "new@example.com" && u.Name == "alice2")), Times.Once);
    }

    [Fact]
    public async Task DeleteUser_WithoutConfirm_Refuses()
    {
        var result = await UserManagementTools.DeleteUser(MockServer.Object, "alice");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteUser(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteUser_WithConfirm_CallsSdk()
    {
        var result = await UserManagementTools.DeleteUser(MockServer.Object, "Alice", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteUser("alice"), Times.Once);
    }

    [Fact]
    public async Task ResetPassword_WithoutConfirm_Refuses()
    {
        var result = await UserManagementTools.ResetPassword(MockServer.Object, "alice", "new-pwd");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.ResetPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_WithConfirm_CallsSdk()
    {
        var result = await UserManagementTools.ResetPassword(MockServer.Object, "Alice", "new-pwd", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.ResetPassword("alice", "new-pwd"), Times.Once);
    }

    [Fact]
    public async Task AddUserToRole_HappyPath_CallsSdk()
    {
        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "Alice", "Admins");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.AddRoleToUser("alice", "Admins"), Times.Once);
    }

    [Fact]
    public async Task RemoveUserFromRole_WithoutConfirm_Refuses()
    {
        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.RemoveRoleFromUser(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveUserFromRole_WithConfirm_CallsSdk()
    {
        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object,
            "Alice", "Admins", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.RemoveRoleFromUser("alice", "Admins"), Times.Once);
    }
}
