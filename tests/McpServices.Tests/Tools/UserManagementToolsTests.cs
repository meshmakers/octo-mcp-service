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
    private void GivenMethodFails(string code, string message = "refused", IReadOnlyList<string>? detailCodes = null)
    {
        var extensions = new Map { ["code"] = code };
        if (detailCodes is { Count: > 0 })
        {
            extensions["details"] = detailCodes
                .Select(c => (object?)new Dictionary<string, object?>
                    { ["code"] = c, ["parameters"] = new Dictionary<string, object?>() })
                .ToList();
        }

        var error = new GraphQLError
        {
            Message = message,
            Extensions = extensions
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

    // ── Password tools: CK methods User.CreateUser-1 / User.ResetPassword-1 (AB#8418) ─────────────────────────

    // Throwaway value, generated per test run; never a real password.
    private readonly string _canary = "canary-" + Guid.NewGuid().ToString("N");

    private JsonElement SentInput() =>
        JsonSerializer.SerializeToElement(_methodRequests.Single().Variables).GetProperty("input");

    private void AssertNoRestPasswordCall()
    {
        MockIdentityClient.Verify(c => c.CreateUser(It.IsAny<UserDto>()), Times.Never);
        MockIdentityClient.Verify(c => c.ResetPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>The response, serialized as the MCP client receives it, does not contain the canary.</summary>
    private void AssertCanaryNotIn(object response) =>
        JsonSerializer.Serialize(response).Should().NotContain(_canary);

    /// <summary>The asset repository answers with the GraphQL validation errors of a schema without the method.</summary>
    private void GivenMethodMissingInSchema(string field)
    {
        var error = new GraphQLError
        {
            Message = $"Cannot query field '{field}' on type 'SystemIdentityUserMutations'.",
            Extensions = new Map { ["code"] = "FIELDS_ON_CORRECT_TYPE", ["number"] = "5.3.1" }
        };
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Callback(new InvocationAction(invocation => _methodRequests.Add((GraphQLRequest)invocation.Arguments[0])))
            .Throws(new QlQueryErrorException("GraphQL error", [error]));
    }

    [Fact]
    public async Task CreateUser_HappyPath_RunsTheCreateUserMethod_LowerCased_NoRestCall()
    {
        GivenMethodSucceeds();

        var result = await UserManagementTools.CreateUser(MockServer.Object, "Alice", "Alice@Example.com", _canary);

        result.IsSuccess.Should().BeTrue();
        result.User.Should().Be("alice");
        result.TenantId.Should().Be(DefaultTenantId);
        result.Message.Should().Be("User 'alice' created.");
        _methodRequests.Single().Query.Should().Be(RtUserMethodClient.CreateUserDocument);
        var input = SentInput();
        input.GetProperty("userName").GetString().Should().Be("alice");
        input.GetProperty("email").GetString().Should().Be("alice@example.com");
        input.GetProperty("password").GetString().Should().Be(_canary);
        AssertNoRestPasswordCall();
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task CreateUser_WithoutPassword_SendsNoPassword()
    {
        GivenMethodSucceeds();

        var result = await UserManagementTools.CreateUser(MockServer.Object, "alice", "alice@example.com");

        result.IsSuccess.Should().BeTrue();
        SentInput().TryGetProperty("password", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(RtUserMethodClient.CreateUserErrors.UserNameReserved, "The user name is reserved.")]
    [InlineData(RtUserMethodClient.CreateUserErrors.UserAlreadyExists, "A user with this name or e-mail address exists.")]
    [InlineData(RtUserMethodClient.CreateUserErrors.PasswordPolicyViolation, "Passwords must have at least one digit.")]
    public async Task CreateUser_DeclaredError_ReturnsCodeAndMessage(string code, string message)
    {
        GivenMethodFails(code, message);

        var result = await UserManagementTools.CreateUser(MockServer.Object, "alice", "alice@example.com", _canary);

        result.IsSuccess.Should().BeFalse();
        result.User.Should().Be("alice");
        result.ErrorMessage.Should().Be($"{code}: {message}");
        AssertNoRestPasswordCall();
        AssertCanaryNotIn(result);
    }

    [Theory]
    [InlineData("METHOD_FORBIDDEN", "The caller needs the role UserManagement.")]
    [InlineData("METHOD_FORBIDDEN", "tenant-not-allowed")]
    public async Task CreateUser_Refused_ReturnsPlatformError_WithoutRestFallback(string code, string message)
    {
        GivenMethodFails(code, message);

        var result = await UserManagementTools.CreateUser(MockServer.Object, "alice", "alice@example.com", _canary);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be($"{code}: {message}");
        AssertNoRestPasswordCall();
    }

    [Fact]
    public async Task ResetPassword_WithoutConfirm_Refuses_WithoutAnyCall()
    {
        var result = await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Refusing to reset password for 'alice' without confirm=true.");
        MockClientFactory.Verify(f => f.CreateTenantClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _methodRequests.Should().BeEmpty();
        AssertNoRestPasswordCall();
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task ResetPassword_WithConfirm_RunsTheResetPasswordMethod_ByRtId_NoRestCall()
    {
        GivenUser();
        GivenMethodSucceeds();

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "Alice", _canary, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.User.Should().Be("alice");
        result.Message.Should().Be("Password reset for user 'alice'.");
        var request = _methodRequests.Single();
        request.Query.Should().Be(RtUserMethodClient.ResetPasswordDocument);
        JsonSerializer.SerializeToElement(request.Variables).GetProperty("rtId").GetString().Should().Be(AliceRtId);
        SentInput().GetProperty("newPassword").GetString().Should().Be(_canary);
        AssertNoRestPasswordCall();
        AssertCanaryNotIn(result);
    }

    [Theory]
    [InlineData(RtUserMethodClient.ResetPasswordErrors.PasswordPolicyViolation, "Passwords must be at least 8 characters.")]
    [InlineData(RtUserMethodClient.ResetPasswordErrors.ShadowUserPasswordNotAllowed, "A cross-tenant shadow user has no password.")]
    [InlineData("METHOD_FORBIDDEN", "The caller needs the role UserManagement.")]
    [InlineData("METHOD_FORBIDDEN", "tenant-not-allowed")]
    [InlineData("METHOD_TARGET_NOT_FOUND", "User not found.")]
    public async Task ResetPassword_Refused_ReturnsCodeAndMessage_WithoutRestFallback(string code, string message)
    {
        GivenUser();
        GivenMethodFails(code, message);

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be($"{code}: {message}");
        AssertNoRestPasswordCall();
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task ResetPassword_UserOfAnotherTenant_IsNotFound_WithoutMethodCall()
    {
        MockIdentityClient
            .Setup(c => c.GetUserByNameEmailOrId("bob"))
            .ThrowsAsync(new ServiceClientException("User 'bob' not found."));

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "bob", _canary, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("bob");
        _methodRequests.Should().BeEmpty();
        AssertNoRestPasswordCall();
    }

    [Theory]
    [InlineData("create", "schema")]
    [InlineData("create", "no-handler")]
    [InlineData("reset", "schema")]
    [InlineData("reset", "no-handler")]
    public async Task ServerWithoutTheMethod_NamesTheNeededIdentityVersion_NoRestCall(string tool, string answer)
    {
        GivenUser();
        if (answer == "schema")
        {
            GivenMethodMissingInSchema(tool == "create" ? "createUser" : "resetPassword");
        }
        else
        {
            GivenMethodFails("METHOD_HANDLER_NOT_AVAILABLE", "No live handler.");
        }

        var result = tool == "create"
            ? await UserManagementTools.CreateUser(MockServer.Object, "alice", "alice@example.com", _canary)
            : await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary, confirm: true);

        result.IsSuccess.Should().BeFalse();
        var version = tool == "create"
            ? UserManagementTools.CreateUserMinimumIdentityVersion
            : UserManagementTools.ResetPasswordMinimumIdentityVersion;
        result.ErrorMessage.Should().Contain($"needs identity services {version} or newer")
            .And.Contain("Methods:Identity:EnablePasswordMethods")
            .And.Contain("does not fall back to the REST endpoint");
        _methodRequests.Should().ContainSingle();
        AssertNoRestPasswordCall();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("reset")]
    public async Task PasswordPolicyViolation_AsTheServerSendsIt_ReturnsTheFailedRuleCodes(string tool)
    {
        // The asset repository answers with "The CK method call failed: <code>" and the failed rules as detail codes
        // only - the one description of the policy it sends (found in the AB#8418 E2E run).
        GivenUser();
        GivenMethodFails("PASSWORD_POLICY_VIOLATION", "The CK method call failed: PASSWORD_POLICY_VIOLATION",
            ["PasswordTooShort", "PasswordRequiresUpper"]);

        var result = tool == "create"
            ? await UserManagementTools.CreateUser(MockServer.Object, "alice", "alice@example.com", _canary)
            : await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(
            "PASSWORD_POLICY_VIOLATION: The CK method call failed: PASSWORD_POLICY_VIOLATION [PasswordTooShort, PasswordRequiresUpper]");
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task ServerEchoingThePassword_IsRedactedInTheResponse()
    {
        GivenUser();
        GivenMethodFails(RtUserMethodClient.ResetPasswordErrors.PasswordPolicyViolation, $"'{_canary}' is too weak.");

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary, confirm: true);

        result.ErrorMessage.Should().Be("PASSWORD_POLICY_VIOLATION: '[REDACTED]' is too weak.");
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task UnexpectedException_EchoingThePassword_IsRedactedInTheResponse()
    {
        GivenUser();
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Throws(new InvalidOperationException($"boom {_canary}"));

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "alice", _canary, confirm: true);

        result.ErrorMessage.Should().Be("boom [REDACTED]");
        AssertCanaryNotIn(result);
    }

    [Fact]
    public async Task UserLookupFailing_WithDeveloperExceptionPage_DoesNotReturnTheCallersToken()
    {
        // AB#8418 E2E: identity answered the lookup of an unknown name with a 500 developer exception page, whose
        // HEADERS section echoes the request's Authorization header; the tool returned it to the MCP client.
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0LW9ubHkifQ.c2lnbmF0dXJlLXRlc3Qtb25seQ";
        var page = "InternalServerError: System.FormatException: 'bob' is not a valid 24 digit hex string.\n" +
                   "   at OctoUserStore.FindByIdAsync()\n\nHEADERS\n=======\nHost: localhost:5003\n" +
                   $"Authorization: bearer {jwt}\ntraceparent: 00-1-2-01\n";
        MockIdentityClient
            .Setup(c => c.GetUserByNameEmailOrId("bob"))
            .ThrowsAsync(new InvalidOperationException(page));

        var result = await UserManagementTools.ResetPassword(MockServer.Object, "bob", _canary, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("InternalServerError: System.FormatException")
            .And.Contain("HEADERS").And.Contain("[request headers removed]")
            .And.NotContain(jwt).And.NotContain("Authorization").And.NotContain("Host: localhost");
        AssertCanaryNotIn(result);
        _methodRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task RoleTools_UserLookupFailing_WithDeveloperExceptionPage_DoNotReturnTheCallersToken(string tool)
    {
        // CodeRabbit (octo-mcp-service#74): the role tools use the same user lookup as reset_user_password.
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0LW9ubHkifQ.c2lnbmF0dXJlLXRlc3Qtb25seQ";
        MockIdentityClient
            .Setup(c => c.GetUserByNameEmailOrId("bob"))
            .ThrowsAsync(new InvalidOperationException(
                $"InternalServerError: boom\n\nHEADERS\n=======\nAuthorization: bearer {jwt}\n"));

        var result = tool == "add"
            ? await UserManagementTools.AddUserToRole(MockServer.Object, "bob", "Admins")
            : await UserManagementTools.RemoveUserFromRole(MockServer.Object, "bob", "Admins", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("InternalServerError: boom").And.NotContain(jwt)
            .And.NotContain("Authorization");
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: [REDACTED]")]
    [InlineData("proxy said bearer abcdefgh12345678 failed", "proxy said bearer [REDACTED] failed")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJhIjoiYiJ9.sig here", "token [REDACTED] here")]
    [InlineData("NotFound: user 'bob' was not found", "NotFound: user 'bob' was not found")]
    public void ErrorTextRedactor_RemovesCredentials_AndKeepsTheRest(string input, string expected) =>
        ErrorTextRedactor.Redact(input).Should().Be(expected);

    [Theory]
    [InlineData(nameof(UserManagementTools.CreateUser), "password")]
    [InlineData(nameof(UserManagementTools.ResetPassword), "newPassword")]
    public void PasswordParameter_IsMarkedSensitive(string method, string parameter)
    {
        var description = typeof(UserManagementTools).GetMethod(method)!.GetParameters()
            .Single(p => p.Name == parameter)
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        description.Should().StartWith("SENSITIVE:").And.Contain("never echoed").And.Contain("never logged");
    }
}
