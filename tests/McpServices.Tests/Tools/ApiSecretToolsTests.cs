using FluentAssertions;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ApiSecretToolsTests : ToolTestBase
{
    private const string ClientId = "ci-deploy";
    private const string ResourceName = "octo_api";

    public ApiSecretToolsTests()
    {
        GivenAuthenticated();
    }

    // ── Client secrets ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetClientSecrets_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetApiSecretsForClient(ClientId))
            .ReturnsAsync([new ApiSecretDto { ValueEncrypted = "v1" }]);

        var result = await ApiSecretTools.GetClientSecrets(MockServer.Object, ClientId);

        result.IsSuccess.Should().BeTrue();
        result.ApiSecrets.Should().HaveCount(1);
        result.OwnerId.Should().Be(ClientId);
    }

    [Fact]
    public async Task GetClientSecrets_MissingId_ReturnsValidationError()
    {
        var result = await ApiSecretTools.GetClientSecrets(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateClientSecret_HappyPath_ReturnsServerGeneratedClearText_AndSendsNoSecretMaterial()
    {
        MockIdentityClient.Setup(c => c.CreateApiSecretForClient(ClientId,
                It.Is<ApiSecretDto>(s => s.ValueClearText == null && s.ValueEncrypted == null
                                         && s.Description == "d")))
            .ReturnsAsync(new ApiSecretDto { ValueEncrypted = "enc-1", ValueClearText = "clear-1" });

        var result = await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, description: "d");

        result.IsSuccess.Should().BeTrue();
        result.ClearTextSecret.Should().Be("clear-1");
        result.SecretValue.Should().Be("enc-1", "the hash identifies the secret for update and delete");
        result.Message.Should().NotContain("clear-1").And.NotContain("enc-1");
    }

    [Fact]
    public async Task CreateClientSecret_MissingArgs_ReturnsValidationError()
    {
        var result = await ApiSecretTools.CreateClientSecret(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateClientSecret_HappyPath_CallsSdk()
    {
        var result = await ApiSecretTools.UpdateClientSecret(MockServer.Object,
            ClientId, "enc-1", description: "updated");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateApiSecretClient(ClientId,
            It.Is<ApiSecretDto>(s => s.ValueEncrypted == "enc-1" && s.Description == "updated")), Times.Once);
    }

    [Fact]
    public async Task DeleteClientSecret_WithoutConfirm_Refuses()
    {
        var result = await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, "enc-1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteApiSecretClient(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteClientSecret_WithConfirm_CallsSdk()
    {
        var result = await ApiSecretTools.DeleteClientSecret(MockServer.Object,
            ClientId, "enc-1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteApiSecretClient(ClientId, "enc-1"), Times.Once);
    }

    // ── API-resource secrets ────────────────────────────────────────────────

    [Fact]
    public async Task GetApiResourceSecrets_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetApiSecretsForApiResource(ResourceName))
            .ReturnsAsync([new ApiSecretDto { ValueEncrypted = "v1" }, new ApiSecretDto { ValueEncrypted = "v2" }]);

        var result = await ApiSecretTools.GetApiResourceSecrets(MockServer.Object, ResourceName);

        result.IsSuccess.Should().BeTrue();
        result.ApiSecrets.Should().HaveCount(2);
        result.OwnerId.Should().Be(ResourceName);
    }

    [Fact]
    public async Task CreateApiResourceSecret_HappyPath_ReturnsServerGeneratedClearText()
    {
        MockIdentityClient.Setup(c => c.CreateApiSecretForApiResource(ResourceName,
                It.Is<ApiSecretDto>(s => s.ValueClearText == null && s.ValueEncrypted == null)))
            .ReturnsAsync(new ApiSecretDto { ValueEncrypted = "enc-2", ValueClearText = "clear-2" });

        var result = await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName);

        result.IsSuccess.Should().BeTrue();
        result.ClearTextSecret.Should().Be("clear-2");
        result.SecretValue.Should().Be("enc-2");
        result.Message.Should().NotContain("clear-2").And.NotContain("enc-2");
    }

    [Fact]
    public async Task CreateApiResourceSecret_MissingName_ReturnsValidationError()
    {
        var result = await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateApiResourceSecret_HappyPath_CallsSdk()
    {
        var result = await ApiSecretTools.UpdateApiResourceSecret(MockServer.Object,
            ResourceName, "enc-1");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateApiSecretApiResource(ResourceName,
            It.Is<ApiSecretDto>(s => s.ValueEncrypted == "enc-1")), Times.Once);
    }

    [Fact]
    public async Task DeleteApiResourceSecret_WithoutConfirm_Refuses()
    {
        var result = await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object, ResourceName, "enc-1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteApiSecretApiResource(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteApiResourceSecret_WithConfirm_CallsSdk()
    {
        var result = await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object,
            ResourceName, "enc-1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteApiSecretApiResource(ResourceName, "enc-1"), Times.Once);
    }

    // ── 403 names the missing role (AB#8094) ────────────────────────────────

    private const string RoleMessage =
        "Access denied: the endpoint requires the role 'SecretManagement' (the caller holds: TenantManagement).";

    private static ServiceClientResultException Forbidden(string? body) =>
        new(body, HttpStatusCode.Forbidden);

    private void GivenEveryWriteIsRefused(ServiceClientResultException exception)
    {
        MockIdentityClient.Setup(c => c.CreateApiSecretForClient(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ThrowsAsync(exception);
        MockIdentityClient.Setup(c => c.UpdateApiSecretClient(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ThrowsAsync(exception);
        MockIdentityClient.Setup(c => c.DeleteApiSecretClient(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(exception);
        MockIdentityClient.Setup(c => c.CreateApiSecretForApiResource(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ThrowsAsync(exception);
        MockIdentityClient.Setup(c => c.UpdateApiSecretApiResource(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ThrowsAsync(exception);
        MockIdentityClient.Setup(c => c.DeleteApiSecretApiResource(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(exception);
    }

    private async Task<ApiSecretResponse[]> CallEveryWrite() =>
    [
        await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId),
        await ApiSecretTools.UpdateClientSecret(MockServer.Object, ClientId, "enc-1"),
        await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, "enc-1", confirm: true),
        await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName),
        await ApiSecretTools.UpdateApiResourceSecret(MockServer.Object, ResourceName, "enc-1"),
        await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object, ResourceName, "enc-1", confirm: true)
    ];

    [Fact]
    public async Task Writes_Forbidden_WithMessageBody_ReturnTheMessageOfIdentity()
    {
        GivenEveryWriteIsRefused(Forbidden("{\"message\":\"" + RoleMessage + "\"}"));

        var errors = await CallEveryWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage == RoleMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"title\":\"Forbidden\",\"status\":403}")]
    [InlineData("{\"message\":\"\"}")]
    [InlineData("not json at all {")]
    public async Task Writes_Forbidden_WithoutUsableMessage_ReturnAGenericAccessDeniedText(string? body)
    {
        GivenEveryWriteIsRefused(Forbidden(body));

        var errors = await CallEveryWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage != null &&
                                         e.ErrorMessage.StartsWith("Access denied (HTTP 403)") &&
                                         e.ErrorMessage.Contains("SecretManagement"));
    }

    [Fact]
    public async Task Writes_OtherErrors_KeepTheExceptionText()
    {
        GivenEveryWriteIsRefused(new ServiceClientResultException("boom", HttpStatusCode.Conflict));

        var errors = await CallEveryWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage == "Conflict: boom");
    }

    [Fact]
    public void WriteToolDescriptions_NameTheSecretManagementRole()
    {
        var writeTools = new[]
        {
            nameof(ApiSecretTools.CreateClientSecret), nameof(ApiSecretTools.UpdateClientSecret),
            nameof(ApiSecretTools.DeleteClientSecret), nameof(ApiSecretTools.CreateApiResourceSecret),
            nameof(ApiSecretTools.UpdateApiResourceSecret), nameof(ApiSecretTools.DeleteApiResourceSecret)
        };

        foreach (var name in writeTools)
        {
            var method = typeof(ApiSecretTools).GetMethod(name)!;
            method.GetCustomAttribute<McpServerToolAttribute>().Should().NotBeNull();
            method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("SecretManagement", name);
        }
    }
}
