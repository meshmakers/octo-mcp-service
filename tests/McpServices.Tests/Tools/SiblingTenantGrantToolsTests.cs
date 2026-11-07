using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8346: sibling-tenant grants through MCP — lookup (identity AB#8341), grant with groups (AB#8343) and the
///     source-tenant allow-list (AB#8345). Happy path, not permitted, not found, missing argument, unauthenticated and the
///     confirm gate; the looked-up identifier is never echoed in a response.
/// </summary>
public class SiblingTenantGrantToolsTests : ToolTestBase
{
    private const string Identifier = "alice@meshmakers.io";
    private const string GroupA = "6a1b2c3d4e5f60718293a4b5";
    private const string GroupB = "6a1b2c3d4e5f60718293a4b6";

    public SiblingTenantGrantToolsTests()
    {
        GivenAuthenticated();
    }

    private static ServiceClientResultException Problem(HttpStatusCode status, string body) =>
        new("ignored", status) { ResponseContent = body };

    private static void AssertNoIdentifier(object response) =>
        System.Text.Json.JsonSerializer.Serialize(response).Should().NotContain(Identifier);

    // ---- lookup_source_tenant_user ----

    [Fact]
    public async Task Lookup_Hit_ReturnsTheUser_WithoutTheIdentifier()
    {
        MockIdentityClient.Setup(c => c.LookupSourceUser(It.Is<SourceUserLookupRequestDto>(r =>
                r.SourceTenantId == "meshmakers" && r.Identifier == Identifier)))
            .ReturnsAsync(new SourceUserLookupResultDto
            {
                SourceTenantId = "meshmakers", UserId = "686a6215fd3e5972ff5693fd", UserName = "alice"
            });

        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, "meshmakers", Identifier);

        result.IsSuccess.Should().BeTrue();
        result.User!.UserName.Should().Be("alice");
        AssertNoIdentifier(result);
    }

    [Fact]
    public async Task Lookup_UniformMiss_ReturnsSourceUserNotFound()
    {
        MockIdentityClient.Setup(c => c.LookupSourceUser(It.IsAny<SourceUserLookupRequestDto>()))
            .ReturnsAsync((SourceUserLookupResultDto?)null);

        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, "meshmakers", Identifier);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("SourceUserNotFound");
        result.ErrorMessage.Should().Be("No user found that can be granted access.");
        AssertNoIdentifier(result);
    }

    [Fact]
    public async Task Lookup_RateLimited_ReturnsCodeAndWaitTime()
    {
        MockIdentityClient.Setup(c => c.LookupSourceUser(It.IsAny<SourceUserLookupRequestDto>()))
            .ThrowsAsync(Problem(HttpStatusCode.TooManyRequests,
                "{\"status\":429,\"title\":\"Too many lookups\",\"code\":\"SourceUserLookupRateLimited\",\"retryAfterSeconds\":240}"));

        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, "meshmakers", Identifier);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("SourceUserLookupRateLimited");
        result.RetryAfterSeconds.Should().Be(240);
        result.ErrorMessage.Should().Contain("Retry after 240 s");
        AssertNoIdentifier(result);
    }

    [Fact]
    public async Task Lookup_NotPermittedToCall_Returns403Readably()
    {
        MockIdentityClient.Setup(c => c.LookupSourceUser(It.IsAny<SourceUserLookupRequestDto>()))
            .ThrowsAsync(new ServiceClientResultException(HttpStatusCode.Forbidden));

        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, "meshmakers", Identifier);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
        AssertNoIdentifier(result);
    }

    [Theory]
    [InlineData("", Identifier)]
    [InlineData("meshmakers", " ")]
    public async Task Lookup_MissingArgument_NoCall(string sourceTenantId, string identifier)
    {
        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, sourceTenantId, identifier);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.LookupSourceUser(It.IsAny<SourceUserLookupRequestDto>()), Times.Never);
    }

    [Fact]
    public async Task Lookup_Unauthenticated_NoCall()
    {
        GivenUnauthenticated();

        var result = await ExternalTenantUserMappingTools.LookupSourceUser(MockServer.Object, "meshmakers", Identifier);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockIdentityClient.Verify(c => c.LookupSourceUser(It.IsAny<SourceUserLookupRequestDto>()), Times.Never);
    }

    // ---- create_external_tenant_user_mapping with groups ----

    [Fact]
    public async Task Grant_WithTwoGroups_ReturnsTheStoredMapping()
    {
        MockIdentityClient.Setup(c => c.GrantExternalTenantUser(It.Is<CreateExternalTenantUserMappingDto>(d =>
                d.GroupIds!.SequenceEqual(new[] { GroupA, GroupB }))))
            .ReturnsAsync(new ExternalTenantUserMappingDto
            {
                Id = new Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId("6a1b2c3d4e5f60718293a4c0"),
                SourceTenantId = "meshmakers", SourceUserName = "alice", GroupNames = ["Operators", "Viewers"]
            });

        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "meshmakers",
            "686a6215fd3e5972ff5693fd", "whatever", groupIds: [GroupA, GroupB]);

        result.IsSuccess.Should().BeTrue();
        result.RtId.Should().Be("6a1b2c3d4e5f60718293a4c0");
        result.Mapping!.GroupNames.Should().Equal("Operators", "Viewers");
        result.Message.Should().Contain("'alice'");
    }

    [Fact]
    public async Task Grant_SourceTenantNotPermitted_ReturnsTheCode()
    {
        MockIdentityClient.Setup(c => c.GrantExternalTenantUser(It.IsAny<CreateExternalTenantUserMappingDto>()))
            .ThrowsAsync(Problem(HttpStatusCode.Forbidden,
                "{\"status\":403,\"title\":\"Source tenant not permitted\",\"code\":\"SourceTenantNotPermitted\"}"));

        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "meshmakers", "u1", "alice",
            groupIds: [GroupA]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("SourceTenantNotPermitted");
        result.ErrorMessage.Should().Be("Source tenant not permitted (HTTP 403, code SourceTenantNotPermitted)");
    }

    [Fact]
    public async Task Grant_UnknownGroup_NamesTheIds()
    {
        MockIdentityClient.Setup(c => c.GrantExternalTenantUser(It.IsAny<CreateExternalTenantUserMappingDto>()))
            .ThrowsAsync(Problem(HttpStatusCode.BadRequest,
                "{\"status\":400,\"title\":\"Unknown group\",\"code\":\"UnknownGroup\",\"unknownGroupIds\":[\"" + GroupB + "\"]}"));

        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "meshmakers", "u1", "alice",
            groupIds: [GroupB]);

        result.ErrorCode.Should().Be("UnknownGroup");
        result.ErrorMessage.Should().Contain(GroupB);
    }

    [Fact]
    public async Task Grant_EmptyGroupEntry_NoCall()
    {
        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "meshmakers", "u1", "alice",
            groupIds: [GroupA, " "]);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.GrantExternalTenantUser(It.IsAny<CreateExternalTenantUserMappingDto>()),
            Times.Never);
    }

    // ---- allow-list ----

    [Fact]
    public async Task GetPermissions_HappyPath()
    {
        MockIdentityClient.Setup(c => c.GetCrossTenantGrantPermissions())
            .ReturnsAsync(new CrossTenantGrantPermissionsDto { TargetTenantIds = ["customer"] });

        var result = await CrossTenantGrantPermissionTools.GetPermissions(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.TargetTenantIds.Should().Equal("customer");
    }

    [Fact]
    public async Task GetPermissions_ForeignAdministrator_Gets403Readably()
    {
        MockIdentityClient.Setup(c => c.GetCrossTenantGrantPermissions())
            .ThrowsAsync(new ServiceClientResultException(HttpStatusCode.Forbidden));

        var result = await CrossTenantGrantPermissionTools.GetPermissions(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task GetPermissions_Unauthenticated_NoCall()
    {
        GivenUnauthenticated();

        var result = await CrossTenantGrantPermissionTools.GetPermissions(MockServer.Object);

        result.ErrorMessage.Should().Contain("Not authenticated");
        MockIdentityClient.Verify(c => c.GetCrossTenantGrantPermissions(), Times.Never);
    }

    [Fact]
    public async Task SetPermissions_HappyPath_ReportsAddedAndRemoved()
    {
        MockIdentityClient.Setup(c => c.GetCrossTenantGrantPermissions())
            .ReturnsAsync(new CrossTenantGrantPermissionsDto { TargetTenantIds = ["old", "customer"] });
        MockIdentityClient.Setup(c => c.UpdateCrossTenantGrantPermissions(
                It.Is<CrossTenantGrantPermissionsDto>(d => d.TargetTenantIds.SequenceEqual(new[] { "customer", "New" })),
                null))
            .ReturnsAsync(new CrossTenantGrantPermissionsDto { TargetTenantIds = ["customer", "new"] });

        var result = await CrossTenantGrantPermissionTools.SetPermissions(MockServer.Object, ["customer", "New"],
            confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.TargetTenantIds.Should().Equal("customer", "new");
        result.Added.Should().Equal("new");
        result.Removed.Should().Equal("old");
    }

    [Fact]
    public async Task SetPermissions_EmptyList_PermitsNobody()
    {
        MockIdentityClient.Setup(c => c.GetCrossTenantGrantPermissions())
            .ReturnsAsync(new CrossTenantGrantPermissionsDto { TargetTenantIds = ["customer"] });
        MockIdentityClient.Setup(c => c.UpdateCrossTenantGrantPermissions(
                It.Is<CrossTenantGrantPermissionsDto>(d => d.TargetTenantIds.Count == 0), null))
            .ReturnsAsync(new CrossTenantGrantPermissionsDto());

        var result = await CrossTenantGrantPermissionTools.SetPermissions(MockServer.Object, [], confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.Removed.Should().Equal("customer");
    }

    [Fact]
    public async Task SetPermissions_WithoutConfirm_Refuses_NoCall()
    {
        var result = await CrossTenantGrantPermissionTools.SetPermissions(MockServer.Object, ["customer"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.Verify(c => c.UpdateCrossTenantGrantPermissions(
            It.IsAny<CrossTenantGrantPermissionsDto>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SetPermissions_MissingList_Refuses_NoCall()
    {
        var result = await CrossTenantGrantPermissionTools.SetPermissions(MockServer.Object, null, confirm: true);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.GetCrossTenantGrantPermissions(), Times.Never);
    }

    [Fact]
    public async Task SetPermissions_ForeignAdministrator_Gets403Readably()
    {
        MockIdentityClient.Setup(c => c.GetCrossTenantGrantPermissions())
            .ThrowsAsync(new ServiceClientResultException(HttpStatusCode.Forbidden));

        var result = await CrossTenantGrantPermissionTools.SetPermissions(MockServer.Object, ["customer"], confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
        MockIdentityClient.Verify(c => c.UpdateCrossTenantGrantPermissions(
            It.IsAny<CrossTenantGrantPermissionsDto>(), It.IsAny<string?>()), Times.Never);
    }

    // ---- risk levels ----

    [Theory]
    [InlineData("lookup_source_tenant_user", McpRiskLevel.Medium)]
    [InlineData("create_external_tenant_user_mapping", McpRiskLevel.High)]
    [InlineData("delete_external_tenant_user_mapping", McpRiskLevel.High)]
    [InlineData("set_cross_tenant_grant_permissions", McpRiskLevel.High)]
    [InlineData("get_cross_tenant_grant_permissions", McpRiskLevel.Low)]
    public void RiskLevels_AsTheStoryDemands(string tool, McpRiskLevel level)
    {
        new ToolRiskRegistry().GetRiskLevel(tool).Should().Be(level);
    }
}
