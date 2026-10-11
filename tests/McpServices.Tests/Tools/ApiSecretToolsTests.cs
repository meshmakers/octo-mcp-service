using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
}
