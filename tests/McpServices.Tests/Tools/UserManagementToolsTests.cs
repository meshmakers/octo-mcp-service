using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
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

    // ── Role tools: CK methods User.AddRole-1 / User.RemoveRole-1 (AB#8440) ──────────────────────────────────

    private const string AliceRtId = "65f1c0ffee0000000000abcd";

    /// <summary>The GraphQL requests the generated RtUserMethodClient sent through the tenant client.</summary>
    private readonly List<GraphQLRequest> _methodRequests = [];

    private void GivenUser(string identifier = "alice", string? rtId = AliceRtId)
    {
        MockIdentityClient
            .Setup(c => c.GetUserByNameEmailOrId(identifier))
            .ReturnsAsync(new UserDto { Name = identifier, UserId = rtId });
    }

    /// <summary>The method call succeeds (answer <c>{ invocationId }</c>).</summary>
    private void GivenMethodSucceeds()
    {
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                _methodRequests.Add((GraphQLRequest)invocation.Arguments[0]);
                var responseType = invocation.Method.GetGenericArguments()[0];
                var response = (CkMethodCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                response.InvocationId = "inv-1";
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                    .Invoke(null, [response])!;
            }));
    }

    /// <summary>The asset repository answers the method call with a GraphQL error carrying <paramref name="code"/>.</summary>
    private void GivenMethodFails(string code, string message = "refused")
    {
        var error = new GraphQLError
        {
            Message = message,
            Extensions = new Map { ["code"] = code }
        };
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Callback(new InvocationAction(invocation => _methodRequests.Add((GraphQLRequest)invocation.Arguments[0])))
            .Throws(new QlQueryErrorException(message, [error]));
    }

    /// <summary>
    ///     Asserts the variables of the single method call field by field: <c>$rtId</c> is the target user and
    ///     <c>$input.roleName</c> the role (wire names of the generated documents, <c>rtId: $rtId, input: $input</c>).
    /// </summary>
    private void AssertSentTarget(string expectedRtId, string expectedRoleName)
    {
        var variables = JsonSerializer.SerializeToElement(_methodRequests.Single().Variables);
        variables.GetProperty("rtId").GetString().Should().Be(expectedRtId);
        variables.GetProperty("input").GetProperty("roleName").GetString().Should().Be(expectedRoleName);
    }

    [Fact]
    public async Task AddUserToRole_HappyPath_CallsUserAddRoleMethod()
    {
        GivenUser();
        GivenMethodSucceeds();

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "Alice", "Admins");

        result.IsSuccess.Should().BeTrue();
        result.User.Should().Be("alice");
        result.TenantId.Should().Be(DefaultTenantId);
        result.Message.Should().Be("User 'alice' assigned to role 'Admins'.");
        MockIdentityClient.Verify(c => c.GetUserByNameEmailOrId("alice"), Times.Once);
        MockClientFactory.Verify(f => f.CreateTenantClient(DefaultTenantId, It.IsAny<string>()), Times.Once);
        var request = _methodRequests.Single();
        request.Query.Should().Be(RtUserMethodClient.AddRoleDocument).And.Contain("rtId: $rtId").And.Contain("input: $input");
        AssertSentTarget(AliceRtId, "Admins");
    }

    [Fact]
    public async Task AddUserToRole_MissingFields_ReturnsValidationError_WithoutCalls()
    {
        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("userNameOrEmail and roleName are required.");
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddUserToRole_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Theory]
    [InlineData(RtUserMethodClient.AddRoleErrors.RoleNotFound, "Role with name 'Admins' not found.")]
    [InlineData(RtUserMethodClient.AddRoleErrors.RoleAlreadyAssigned, "User 'alice' already has role 'Admins'.")]
    public async Task AddUserToRole_DeclaredError_ReturnsFailureWithText(string code, string expected)
    {
        GivenUser();
        GivenMethodFails(code);

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.User.Should().Be("alice");
        result.ErrorMessage.Should().Be(expected);
    }

    [Fact]
    public async Task AddUserToRole_Forbidden_ReturnsPlatformError()
    {
        GivenUser();
        GivenMethodFails("METHOD_FORBIDDEN", "The caller may not run User.AddRole.");

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("METHOD_FORBIDDEN: The caller may not run User.AddRole.");
    }

    [Fact]
    public async Task AddUserToRole_UnknownUser_ReturnsLookupError_WithoutMethodCall()
    {
        MockIdentityClient
            .Setup(c => c.GetUserByNameEmailOrId("ghost"))
            .ThrowsAsync(new ServiceClientException("User 'ghost' not found."));

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "ghost", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ghost");
        _methodRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddUserToRole_UserWithoutRtId_ReturnsNotFound_WithoutMethodCall()
    {
        GivenUser(rtId: null);
        GivenMethodSucceeds();

        var result = await UserManagementTools.AddUserToRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("User 'alice' not found.");
        _methodRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveUserFromRole_WithoutConfirm_Refuses()
    {
        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object, "alice", "Admins");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Refusing to remove role 'Admins' from 'alice' without confirm=true.");
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        MockIdentityClient.Verify(c => c.GetUserByNameEmailOrId(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveUserFromRole_WithConfirm_CallsUserRemoveRoleMethod()
    {
        GivenUser();
        GivenMethodSucceeds();

        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object,
            "Alice", "Admins", confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.User.Should().Be("alice");
        result.Message.Should().Be("User 'alice' removed from role 'Admins'.");
        _methodRequests.Single().Query.Should().Be(RtUserMethodClient.RemoveRoleDocument);
        AssertSentTarget(AliceRtId, "Admins");
    }

    [Theory]
    [InlineData(RtUserMethodClient.RemoveRoleErrors.RoleNotFound, "Role with name 'Admins' not found.")]
    [InlineData(RtUserMethodClient.RemoveRoleErrors.RoleNotAssigned,
        "User 'alice' doesn't have role 'Admins' as a direct role.")]
    public async Task RemoveUserFromRole_DeclaredError_ReturnsFailureWithText(string code, string expected)
    {
        GivenUser();
        GivenMethodFails(code);

        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object, "alice", "Admins", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(expected);
    }

    [Fact]
    public async Task RemoveUserFromRole_TargetNotFound_ReturnsPlatformError()
    {
        GivenUser();
        GivenMethodFails("METHOD_TARGET_NOT_FOUND", "User not found.");

        var result = await UserManagementTools.RemoveUserFromRole(MockServer.Object, "alice", "Admins", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("METHOD_TARGET_NOT_FOUND: User not found.");
    }

    [Fact]
    public void DescribeRoleError_UnknownDeclaredCode_FallsBackToCodeAndMessage()
    {
        var error = new CkMethodError("SOMETHING_NEW", "Something new happened.", new Dictionary<string, string>(), []);

        UserManagementTools.DescribeRoleError(error, "alice", "Admins")
            .Should().Be("SOMETHING_NEW: Something new happened.");
    }
}
