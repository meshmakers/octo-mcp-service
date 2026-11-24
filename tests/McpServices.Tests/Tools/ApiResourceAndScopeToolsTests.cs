using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ApiResourceToolsTests : ToolTestBase
{
    public ApiResourceToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetApiResources_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetApiResources())
            .ReturnsAsync([new ApiResourceDto { Name = "octo_api" }]);

        var result = await ApiResourceTools.GetApiResources(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.ApiResources.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetApiResources_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ApiResourceTools.GetApiResources(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateApiResource_HappyPath_PassesScopes()
    {
        CkMethods.Succeeds();

        await ApiResourceTools.CreateApiResource(MockServer.Object,
            name: "octo_api", displayName: "Octo API", scopes: ["read", "write"]);

        var input = CkMethods.InputOf("SystemIdentityApiResourceCreateApiResource");
        CkMethodFake.Str(input, "name").Should().Be("octo_api");
        CkMethodFake.Str(input, "displayName").Should().Be("Octo API");
        CkMethodFake.Strings(input.GetProperty("scopes")).Should().Equal("read", "write");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateApiResource));
    }

    [Fact]
    public async Task CreateApiResource_MissingName_ReturnsValidationError()
    {
        var result = await ApiResourceTools.CreateApiResource(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateApiResource_HappyPath_CallsSdk()
    {
        CkMethods.GivenQueryReturns(new RtApiResourceDto { RtId = new OctoObjectId(ResourceRtId), Name = "octo_api" });

        var result = await ApiResourceTools.UpdateApiResource(MockServer.Object,
            "octo_api", displayName: "Octo Public");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiResourceUpdateApiResource").Should().Be(ResourceRtId);
        var input = CkMethods.InputOf("SystemIdentityApiResourceUpdateApiResource");
        CkMethodFake.Str(input, "name").Should().Be("octo_api");
        CkMethodFake.Str(input, "displayName").Should().Be("Octo Public");
    }

    [Fact]
    public async Task DeleteApiResource_WithoutConfirm_Refuses()
    {
        var result = await ApiResourceTools.DeleteApiResource(MockServer.Object, "octo_api");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteApiResource));
    }

    [Fact]
    public async Task DeleteApiResource_WithConfirm_CallsSdk()
    {
        CkMethods.GivenQueryReturns(new RtApiResourceDto { RtId = new OctoObjectId(ResourceRtId), Name = "octo_api" });

        var result = await ApiResourceTools.DeleteApiResource(MockServer.Object,
            "octo_api", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiResourceDeleteApiResource").Should().Be(ResourceRtId);
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteApiResource));
    }

    [Fact]
    public async Task DeleteApiResource_Unknown_IsReportedAsNotFound_NoMethodCall()
    {
        CkMethods.GivenQueryReturns<RtApiResourceDto>();

        var result = await ApiResourceTools.DeleteApiResource(MockServer.Object, "ghost", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("API resource 'ghost' not found.");
        CkMethods.Requests.Should().BeEmpty();
    }

    private const string ResourceRtId = "507f1f77bcf86cd799439088";
}

public class ApiScopeToolsTests : ToolTestBase
{
    public ApiScopeToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetApiScopes_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetApiScopes())
            .ReturnsAsync([new ApiScopeDto { Name = "read" }, new ApiScopeDto { Name = "write" }]);

        var result = await ApiScopeTools.GetApiScopes(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.ApiScopes.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetApiScopes_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ApiScopeTools.GetApiScopes(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateApiScope_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        await ApiScopeTools.CreateApiScope(MockServer.Object,
            "read", displayName: "Read", description: "read scope");

        var input = CkMethods.InputOf("SystemIdentityApiScopeCreateApiScope");
        CkMethodFake.Str(input, "name").Should().Be("read");
        CkMethodFake.Str(input, "displayName").Should().Be("Read");
        CkMethodFake.Str(input, "description").Should().Be("read scope");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateApiScope));
    }

    [Fact]
    public async Task CreateApiScope_MissingName_ReturnsValidationError()
    {
        var result = await ApiScopeTools.CreateApiScope(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateApiScope_HappyPath_CallsSdk()
    {
        CkMethods.GivenQueryReturns(new RtApiScopeDto { RtId = new OctoObjectId(ScopeRtId), Name = "read" });

        var result = await ApiScopeTools.UpdateApiScope(MockServer.Object,
            "read", displayName: "Read Access");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiScopeUpdateApiScope").Should().Be(ScopeRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityApiScopeUpdateApiScope"), "displayName")
            .Should().Be("Read Access");
    }

    [Fact]
    public async Task DeleteApiScope_WithoutConfirm_Refuses()
    {
        var result = await ApiScopeTools.DeleteApiScope(MockServer.Object, "read");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteScope));
    }

    [Fact]
    public async Task DeleteApiScope_WithConfirm_CallsSdk()
    {
        CkMethods.GivenQueryReturns(new RtApiScopeDto { RtId = new OctoObjectId(ScopeRtId), Name = "read" });

        var result = await ApiScopeTools.DeleteApiScope(MockServer.Object, "read", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiScopeDeleteApiScope").Should().Be(ScopeRtId);
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteScope));
    }

    private const string ScopeRtId = "507f1f77bcf86cd799439077";
}
