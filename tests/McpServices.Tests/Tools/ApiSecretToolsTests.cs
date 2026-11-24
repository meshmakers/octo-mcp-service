using FluentAssertions;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ApiSecretToolsTests : ToolTestBase
{
    private const string ClientId = "ci-deploy";
    private const string ResourceName = "octo_api";

    private const string ClientRtId = "65f1c0ffee0000000000c3c3";
    private const string ResourceRtId = "65f1c0ffee0000000000e3e3";

    public ApiSecretToolsTests()
    {
        GivenAuthenticated();
        // The secret methods address the owner by runtime id: client by REST read, API resource by GraphQL query.
        MockIdentityClient.Setup(c => c.GetClient(ClientId))
            .ReturnsAsync(new ClientDto { ClientId = ClientId, RtId = ClientRtId });
        CkMethods.GivenQueryReturns(new RtApiResourceDto { RtId = new OctoObjectId(ResourceRtId), Name = ResourceName });
    }

    /// <summary>The stored value of a generated secret: Base64 SHA-256 of the UTF-8 clear text.</summary>
    private static string StoredValueOf(string clearText) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(clearText)));

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
        // Client.AddSecret-1 generates the secret on the server and answers it once.
        CkMethods.Succeeds("clear-1");

        var result = await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, description: "d", confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.ClearTextSecret.Should().Be("clear-1");
        result.SecretValue.Should().Be(StoredValueOf("clear-1"), "the hash identifies the secret for update and delete");
        result.Message.Should().NotContain("clear-1").And.NotContain(StoredValueOf("clear-1"));
        CkMethods.RtIdOf("SystemIdentityClientAddSecret").Should().Be(ClientRtId);
        var input = CkMethods.InputOf("SystemIdentityClientAddSecret");
        CkMethodFake.Str(input, "description").Should().Be("d");
        input.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["description", "expirationDate"],
            "the tool sends no secret material");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateApiSecretForClient));
    }

    [Fact]
    public async Task CreateSecret_WithoutConfirm_IsRefused_BeforeAnyCall()
    {
        CkMethods.Succeeds();

        var client = await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId);
        var resource = await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName);

        client.IsSuccess.Should().BeFalse();
        client.ErrorMessage.Should().Contain("without confirm=true");
        resource.IsSuccess.Should().BeFalse();
        resource.ErrorMessage.Should().Contain("without confirm=true");
        CkMethods.OperationNames.Should().BeEmpty();
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
        CkMethods.Succeeds();

        var result = await ApiSecretTools.UpdateClientSecret(MockServer.Object,
            ClientId, "enc-1", description: "updated");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientUpdateSecret").Should().Be(ClientRtId);
        var input = CkMethods.InputOf("SystemIdentityClientUpdateSecret");
        CkMethodFake.Str(input, "secretValue").Should().Be("enc-1");
        CkMethodFake.Str(input, "description").Should().Be("updated");
    }

    [Fact]
    public async Task DeleteClientSecret_WithoutConfirm_Refuses()
    {
        var result = await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, "enc-1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteApiSecretClient));
    }

    [Fact]
    public async Task DeleteClientSecret_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ApiSecretTools.DeleteClientSecret(MockServer.Object,
            ClientId, "enc-1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientRemoveSecret").Should().Be(ClientRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityClientRemoveSecret"), "secretValue").Should().Be("enc-1");
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
        CkMethods.Succeeds("clear-2");

        var result = await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.ClearTextSecret.Should().Be("clear-2");
        result.SecretValue.Should().Be(StoredValueOf("clear-2"));
        result.Message.Should().NotContain("clear-2").And.NotContain(StoredValueOf("clear-2"));
        CkMethods.RtIdOf("SystemIdentityApiResourceAddSecret").Should().Be(ResourceRtId);
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
        CkMethods.Succeeds();

        var result = await ApiSecretTools.UpdateApiResourceSecret(MockServer.Object,
            ResourceName, "enc-1");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiResourceUpdateSecret").Should().Be(ResourceRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityApiResourceUpdateSecret"), "secretValue").Should().Be("enc-1");
    }

    [Fact]
    public async Task DeleteApiResourceSecret_WithoutConfirm_Refuses()
    {
        var result = await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object, ResourceName, "enc-1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteApiSecretApiResource));
    }

    [Fact]
    public async Task DeleteApiResourceSecret_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object,
            ResourceName, "enc-1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityApiResourceRemoveSecret").Should().Be(ResourceRtId);
    }

    // ── 403 names the missing role (AB#8094) ────────────────────────────────

    private const string RoleMessage =
        "Access denied: the endpoint requires the role 'SecretManagement' (the caller holds: TenantManagement).";

    private static ServiceClientResultException Forbidden(string? body) =>
        new(body, HttpStatusCode.Forbidden);

    private async Task<ApiSecretResponse[]> CallEveryWrite() =>
    [
        await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, confirm: true),
        await ApiSecretTools.UpdateClientSecret(MockServer.Object, ClientId, "enc-1"),
        await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, "enc-1", confirm: true),
        await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName, confirm: true),
        await ApiSecretTools.UpdateApiResourceSecret(MockServer.Object, ResourceName, "enc-1"),
        await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object, ResourceName, "enc-1", confirm: true)
    ];

    private async Task<ApiSecretResponse[]> CallEveryClientWrite() =>
    [
        await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, confirm: true),
        await ApiSecretTools.UpdateClientSecret(MockServer.Object, ClientId, "enc-1"),
        await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, "enc-1", confirm: true)
    ];

    [Fact]
    public async Task Writes_Forbidden_ReturnTheCodeAndTheMessageOfIdentity_NoRestCall()
    {
        // The method gateway refuses a caller without SecretManagement (or with a token of another tenant).
        CkMethods.Fails("METHOD_FORBIDDEN", RoleMessage);

        var errors = await CallEveryWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage == "METHOD_FORBIDDEN: " + RoleMessage);
        CkMethods.Requests.Should().HaveCount(6, "one method call per tool and nothing else");
        MockIdentityClient.Invocations.Select(i => i.Method.Name).Should().OnlyContain(n => n.StartsWith("Get"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"title\":\"Forbidden\",\"status\":403}")]
    [InlineData("{\"message\":\"\"}")]
    [InlineData("not json at all {")]
    public async Task ClientLookup_Forbidden_WithoutUsableMessage_ReturnsAGenericAccessDeniedText(string? body)
    {
        MockIdentityClient.Setup(c => c.GetClient(ClientId)).ThrowsAsync(Forbidden(body));

        var errors = await CallEveryClientWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage != null &&
                                         e.ErrorMessage.StartsWith("Access denied (HTTP 403)") &&
                                         e.ErrorMessage.Contains("SecretManagement"));
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ClientLookup_OtherErrors_KeepTheExceptionText()
    {
        MockIdentityClient.Setup(c => c.GetClient(ClientId))
            .ThrowsAsync(new ServiceClientResultException("boom", HttpStatusCode.Conflict));

        var errors = await CallEveryClientWrite();

        errors.Should().OnlyContain(e => !e.IsSuccess && e.ErrorMessage == "Conflict: boom");
    }

    // ── Secrets: shown once, never in a message (canaries) ──────────────────

    private const string Canary = "canary-Zx8Qw2Lp9Vt4Rk7Hs3Nm";

    [Fact]
    public async Task GeneratedSecret_IsShownOnlyInClearTextSecret()
    {
        CkMethods.Succeeds(Canary);

        var client = await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, confirm: true);
        var resource = await ApiSecretTools.CreateApiResourceSecret(MockServer.Object, ResourceName, confirm: true);

        foreach (var result in new[] { client, resource })
        {
            result.ClearTextSecret.Should().Be(Canary);
            result.Message.Should().NotContain(Canary);
            result.ErrorMessage.Should().BeNull();
            CountOf(JsonSerializer.Serialize(result), Canary).Should().Be(1, "the secret is shown exactly once");
        }
    }

    [Theory]
    [InlineData("SECRET_NOT_FOUND")]
    [InlineData("METHOD_VALIDATION_FAILED")]
    [InlineData("METHOD_FORBIDDEN")]
    public async Task SecretValueEchoedByTheServer_IsRedactedInEveryErrorMessage(string code)
    {
        CkMethods.Fails(code, $"No secret '{Canary}'.", new Dictionary<string, string> { ["secretValue"] = Canary });

        var results = new[]
        {
            await ApiSecretTools.UpdateClientSecret(MockServer.Object, ClientId, Canary),
            await ApiSecretTools.DeleteClientSecret(MockServer.Object, ClientId, Canary, confirm: true),
            await ApiSecretTools.UpdateApiResourceSecret(MockServer.Object, ResourceName, Canary),
            await ApiSecretTools.DeleteApiResourceSecret(MockServer.Object, ResourceName, Canary, confirm: true)
        };

        foreach (var result in results)
        {
            result.IsSuccess.Should().BeFalse();
            result.ErrorMessage.Should().StartWith(code).And.Contain("[REDACTED]").And.NotContain(Canary);
            result.Message.Should().BeNull();
        }
    }

    [Fact]
    public async Task UnexpectedException_EchoingTheSecretValue_IsRedacted()
    {
        CkMethods.Throws(new InvalidOperationException($"boom {Canary}"));

        var result = await ApiSecretTools.UpdateClientSecret(MockServer.Object, ClientId, Canary);

        result.ErrorMessage.Should().Be("boom [REDACTED]");
    }

    [Fact]
    public async Task SecretMethodNotAvailable_NamesTheBusSwitch_NoRestCall()
    {
        CkMethods.HandlerNotAvailable();

        var result = await ApiSecretTools.CreateClientSecret(MockServer.Object, ClientId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Identity 3.8").And.Contain("Methods:Identity:EnablePasswordMethods");
        result.ClearTextSecret.Should().BeNull();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateApiSecretForClient));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
