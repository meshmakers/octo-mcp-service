using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class CkModelLibraryToolsTests : ToolTestBase
{
    public CkModelLibraryToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task ListCatalogs_HappyPath_ReturnsList()
    {
        MockAssetClient.Setup(c => c.GetCkModelCatalogsAsync())
            .ReturnsAsync([new CkModelCatalogDto { Name = "PublicGitHubCatalog" }]);

        var result = await CkModelLibraryTools.ListCatalogs(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Catalogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListCatalogs_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await CkModelLibraryTools.ListCatalogs(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ListCatalogModels_HappyPath_PassesFilters()
    {
        MockAssetClient.Setup(c => c.ListCkModelCatalogModelsAsync("cat", "energy", 0, 50))
            .ReturnsAsync(new CkModelCatalogListResponseDto
            {
                Items = [new CkModelCatalogItemDto { Id = "Energy-1", Name = "Energy" }],
                TotalCount = 1
            });

        var result = await CkModelLibraryTools.ListCatalogModels(MockServer.Object, "cat", "energy", 0, 50);

        result.IsSuccess.Should().BeTrue();
        result.Models!.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task RefreshCatalogs_NoCatalogName_CallsSdkWithNull()
    {
        var result = await CkModelLibraryTools.RefreshCatalogs(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.RefreshCkModelCatalogsAsync(null), Times.Once);
    }

    [Fact]
    public async Task RefreshCatalogs_SpecificCatalog_CallsSdk()
    {
        var result = await CkModelLibraryTools.RefreshCatalogs(MockServer.Object, "myCat");

        result.IsSuccess.Should().BeTrue();
        result.CatalogName.Should().Be("myCat");
        MockAssetClient.Verify(c => c.RefreshCkModelCatalogsAsync("myCat"), Times.Once);
    }

    [Fact]
    public async Task GetLibraryStatus_HappyPath_ReturnsStatus()
    {
        MockAssetClient.Setup(c => c.GetLibraryStatusAsync(DefaultTenantId))
            .ReturnsAsync(new CkModelLibraryStatusResponseDto
            {
                Items = [new CkModelLibraryStatusItemDto { Name = "Industry.Energy" }],
                ModelsNeedingActionCount = 0
            });

        var result = await CkModelLibraryTools.GetLibraryStatus(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Status!.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task CheckDependencies_MissingFields_ReturnsValidationError()
    {
        var result = await CkModelLibraryTools.CheckDependencies(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CheckDependencies_HappyPath_PassesBatchOfOne()
    {
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.Is<List<ImportFromCatalogRequestDto>>(l =>
                    l.Count == 1 && l[0].CatalogName == "cat" && l[0].ModelId == "Industry.Energy-2.0.0")))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto
            {
                ModelsToImport = ["Industry.Energy-2.0.0", "Industry.Basic-2.0.0"]
            });

        var result = await CkModelLibraryTools.CheckDependencies(MockServer.Object,
            "cat", "Industry.Energy-2.0.0");

        result.IsSuccess.Should().BeTrue();
        result.Resolution!.ModelsToImport.Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckUpgrade_BreakingChanges_FormatsBreakingNotice()
    {
        MockAssetClient.Setup(c => c.CheckUpgradeAsync(DefaultTenantId,
                It.Is<ImportFromCatalogRequestDto>(r => r.ModelId == "Energy-3.0.0")))
            .ReturnsAsync(new UpgradeCheckResponseDto
            {
                UpgradeNeeded = true,
                InstalledVersion = "Energy-2.0.0",
                TargetVersion = "Energy-3.0.0",
                HasBreakingChanges = true
            });

        var result = await CkModelLibraryTools.CheckUpgrade(MockServer.Object, "cat", "Energy-3.0.0");

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("BREAKING CHANGES");
    }

    [Fact]
    public async Task ImportFromCatalog_NothingToDo_ReturnsSuccessWithoutJobs()
    {
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.IsAny<List<ImportFromCatalogRequestDto>>()))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto { ModelsToImport = [] });

        var result = await CkModelLibraryTools.ImportFromCatalog(MockServer.Object, "cat", "Energy-1");

        result.IsSuccess.Should().BeTrue();
        result.JobIds.Should().BeEmpty();
        result.Message.Should().Contain("already up to date");
        MockAssetClient.Verify(c => c.ImportFromCatalogBatchAsync(
            It.IsAny<string>(), It.IsAny<ImportFromCatalogBatchRequestDto>()), Times.Never);
    }

    [Fact]
    public async Task ImportFromCatalog_HappyPath_ResolvesDepsThenImports()
    {
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.IsAny<List<ImportFromCatalogRequestDto>>()))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto
            {
                ModelsToImport = ["Energy-2.0.0", "Basic-2.0.0"]
            });
        MockAssetClient.Setup(c => c.ImportFromCatalogBatchAsync(DefaultTenantId,
                It.Is<ImportFromCatalogBatchRequestDto>(r =>
                    r.CatalogName == "cat" && r.ModelIds.Count == 2)))
            .ReturnsAsync(new BatchImportResponseDto { JobIds = ["job1", "job2"] });

        var result = await CkModelLibraryTools.ImportFromCatalog(MockServer.Object, "cat", "Energy-2.0.0");

        result.IsSuccess.Should().BeTrue();
        result.JobIds.Should().Equal("job1", "job2");
        result.ModelsToImport.Should().HaveCount(2);
    }

    [Fact]
    public async Task FixAllModels_NothingNeedsAction_ReturnsEarly()
    {
        MockAssetClient.Setup(c => c.GetLibraryStatusAsync(DefaultTenantId))
            .ReturnsAsync(new CkModelLibraryStatusResponseDto { Items = [], ModelsNeedingActionCount = 0 });

        var result = await CkModelLibraryTools.FixAllModels(MockServer.Object, confirm: false);

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("up to date");
        MockAssetClient.Verify(c => c.ResolveDependenciesBatchAsync(
            It.IsAny<string>(), It.IsAny<List<ImportFromCatalogRequestDto>>()), Times.Never);
    }

    [Fact]
    public async Task FixAllModels_NeedsConfirm_ReturnsModelList()
    {
        MockAssetClient.Setup(c => c.GetLibraryStatusAsync(DefaultTenantId))
            .ReturnsAsync(new CkModelLibraryStatusResponseDto
            {
                Items =
                [
                    new CkModelLibraryStatusItemDto
                    {
                        Name = "Energy",
                        NeedsAction = true,
                        IsServiceManaged = false,
                        IsCompatible = true,
                        CatalogName = "cat",
                        FullModelId = "Energy-2.0.0"
                    }
                ],
                ModelsNeedingActionCount = 1
            });
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.IsAny<List<ImportFromCatalogRequestDto>>()))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto { ModelsToImport = ["Energy-2.0.0"] });

        var result = await CkModelLibraryTools.FixAllModels(MockServer.Object, confirm: false);

        result.IsSuccess.Should().BeFalse();
        result.ModelsToImport.Should().Contain("Energy-2.0.0");
        result.ErrorMessage.Should().Contain("confirm=true").And.Contain("Energy-2.0.0");
        MockAssetClient.Verify(c => c.ImportFromCatalogBatchAsync(
            It.IsAny<string>(), It.IsAny<ImportFromCatalogBatchRequestDto>()), Times.Never);
    }

    [Fact]
    public async Task FixAllModels_WithConfirm_EnqueuesImports()
    {
        MockAssetClient.Setup(c => c.GetLibraryStatusAsync(DefaultTenantId))
            .ReturnsAsync(new CkModelLibraryStatusResponseDto
            {
                Items =
                [
                    new CkModelLibraryStatusItemDto
                    {
                        Name = "Energy",
                        NeedsAction = true,
                        IsServiceManaged = false,
                        IsCompatible = true,
                        CatalogName = "cat",
                        FullModelId = "Energy-2.0.0"
                    }
                ],
                ModelsNeedingActionCount = 1
            });
        MockAssetClient.Setup(c => c.ResolveDependenciesBatchAsync(DefaultTenantId,
                It.IsAny<List<ImportFromCatalogRequestDto>>()))
            .ReturnsAsync(new BatchDependencyResolutionResponseDto { ModelsToImport = ["Energy-2.0.0"] });
        MockAssetClient.Setup(c => c.ImportFromCatalogBatchAsync(DefaultTenantId,
                It.IsAny<ImportFromCatalogBatchRequestDto>()))
            .ReturnsAsync(new BatchImportResponseDto { JobIds = ["job1"] });

        var result = await CkModelLibraryTools.FixAllModels(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.JobIds.Should().Contain("job1");
    }
}
