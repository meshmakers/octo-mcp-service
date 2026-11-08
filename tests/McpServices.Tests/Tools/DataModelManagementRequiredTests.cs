using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8529: the asset repository refuses a model-changing call of a caller without the tenant role
///     <c>DataModelManagement</c> with <c>403</c> and <c>{"message", "code": "DataModelManagementRequired"}</c>. The
///     code and the reason reach the tool result of every model-changing tool.
/// </summary>
public class DataModelManagementRequiredTests : ToolTestBase
{
    private const string Code = "DataModelManagementRequired";

    private const string Body =
        """{"message":"Access is denied: this operation requires the role 'DataModelManagement' in the tenant. The token carries no such role; ask a tenant administrator to grant it.","code":"DataModelManagementRequired"}""";

    public DataModelManagementRequiredTests()
    {
        GivenAuthenticated();
    }

    private static ServiceClientResultException Forbidden() =>
        new("Forbidden", HttpStatusCode.Forbidden) { ResponseContent = Body };

    private (string transferId, string filePath) StageUpload()
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, "model.json");
        File.WriteAllText(path, "{}");
        FileTransferStore.CompleteUpload(id, 2);
        return (id, path);
    }

    private static void AssertRefused(bool isSuccess, string? errorCode, string? errorMessage)
    {
        isSuccess.Should().BeFalse();
        errorCode.Should().Be(Code);
        errorMessage.Should().Contain("403").And.Contain(Code);
    }

    [Fact]
    public async Task ImportCkModel_WithoutTheRole_ReportsTheCode()
    {
        var (transferId, _) = StageUpload();
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>())).ThrowsAsync(Forbidden());

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, transferId);

        AssertRefused(result.IsSuccess, result.ErrorCode, result.ErrorMessage);
    }

    [Fact]
    public async Task InstallBlueprint_WithoutTheRole_ReportsTheCode()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", false)).ThrowsAsync(Forbidden());

        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.0.0");

        AssertRefused(result.IsSuccess, result.ErrorCode, result.ErrorMessage);
    }

    [Fact]
    public async Task UninstallBlueprint_WithoutTheRole_ReportsTheCode()
    {
        MockAssetClient.Setup(c => c.UninstallBlueprintAsync(DefaultTenantId, "B", It.IsAny<bool>()))
            .ThrowsAsync(Forbidden());

        var result = await BlueprintTools.UninstallBlueprint(MockServer.Object, "B", confirm: true);

        AssertRefused(result.IsSuccess, result.ErrorCode, result.ErrorMessage);
    }

    [Fact]
    public async Task RefreshCkCatalogs_WithoutTheRole_ReportsTheCode()
    {
        MockAssetClient.Setup(c => c.RefreshCkModelCatalogsAsync(It.IsAny<string?>())).ThrowsAsync(Forbidden());

        var result = await CkModelLibraryTools.RefreshCatalogs(MockServer.Object);

        AssertRefused(result.IsSuccess, result.ErrorCode, result.ErrorMessage);
    }

    [Fact]
    public async Task ImportFromCatalog_WithoutTheRole_ReportsTheCode()
    {
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.IsAny<List<ImportFromCatalogRequestDto>>()))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto { ModelsToImport = ["Basic-2.5.0"] });
        MockAssetClient.Setup(c => c.ImportFromCatalogBatchAsync(DefaultTenantId,
                It.IsAny<ImportFromCatalogBatchRequestDto>()))
            .ThrowsAsync(Forbidden());

        var result = await CkModelLibraryTools.ImportFromCatalog(MockServer.Object, "Public", "Basic-2.5.0");

        AssertRefused(result.IsSuccess, result.ErrorCode, result.ErrorMessage);
    }

    [Fact]
    public async Task OtherFailures_KeepTheirMessage_WithoutACode()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", false))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.0.0");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().Be("boom");
    }
}
