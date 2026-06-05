using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
        await ApiResourceTools.CreateApiResource(MockServer.Object,
            name: "octo_api", displayName: "Octo API", scopes: ["read", "write"]);

        MockIdentityClient.Verify(c => c.CreateApiResource(It.Is<ApiResourceDto>(d =>
            d.Name == "octo_api" && d.DisplayName == "Octo API" &&
            d.Scopes!.Contains("read") && d.Scopes.Contains("write"))), Times.Once);
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
        var result = await ApiResourceTools.UpdateApiResource(MockServer.Object,
            "octo_api", displayName: "Octo Public");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateApiResource("octo_api",
            It.Is<ApiResourceDto>(d => d.DisplayName == "Octo Public")), Times.Once);
    }

    [Fact]
    public async Task DeleteApiResource_WithoutConfirm_Refuses()
    {
        var result = await ApiResourceTools.DeleteApiResource(MockServer.Object, "octo_api");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteApiResource(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteApiResource_WithConfirm_CallsSdk()
    {
        var result = await ApiResourceTools.DeleteApiResource(MockServer.Object,
            "octo_api", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteApiResource("octo_api"), Times.Once);
    }
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
        await ApiScopeTools.CreateApiScope(MockServer.Object,
            "read", displayName: "Read", description: "read scope");

        MockIdentityClient.Verify(c => c.CreateApiScope(It.Is<ApiScopeDto>(d =>
            d.Name == "read" && d.DisplayName == "Read" && d.Description == "read scope")), Times.Once);
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
        var result = await ApiScopeTools.UpdateApiScope(MockServer.Object,
            "read", displayName: "Read Access");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateApiScope("read",
            It.Is<ApiScopeDto>(d => d.DisplayName == "Read Access")), Times.Once);
    }

    [Fact]
    public async Task DeleteApiScope_WithoutConfirm_Refuses()
    {
        var result = await ApiScopeTools.DeleteApiScope(MockServer.Object, "read");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteScope(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteApiScope_WithConfirm_CallsSdk()
    {
        var result = await ApiScopeTools.DeleteApiScope(MockServer.Object, "read", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteScope("read"), Times.Once);
    }
}
