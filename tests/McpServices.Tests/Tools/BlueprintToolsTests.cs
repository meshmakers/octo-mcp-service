using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class BlueprintToolsTests : ToolTestBase
{
    public BlueprintToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task ListBlueprints_HappyPath_ReturnsCatalog()
    {
        MockAssetClient.Setup(c => c.ListBlueprintsAsync(0, 100))
            .ReturnsAsync(new BlueprintCatalogListResponseDto
            {
                Items = [new BlueprintCatalogItemDto { Id = "B-1.0.0", Name = "B", Version = "1.0.0" }],
                TotalCount = 1,
                Skip = 0,
                Take = 100
            });

        var result = await BlueprintTools.ListBlueprints(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Catalog!.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListBlueprints_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await BlueprintTools.ListBlueprints(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task InstallBlueprint_HappyPath_CallsSdk()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", false))
            .ReturnsAsync(new BlueprintApplyResultDto
            {
                Success = true,
                ApplicationMode = "Install",
                SeedDataFilesApplied = 3,
                LoadedCkModels = ["m1"]
            });

        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.0.0");

        result.IsSuccess.Should().BeTrue();
        result.Result!.Success.Should().BeTrue();
        result.Result.SeedDataFilesApplied.Should().Be(3);
    }

    private static BlueprintArchiveActivationDto[] SampleActivations() =>
    [
        new()
        {
            ArchiveRtId = "6a4aa4cc22664f2b26c67e6b", WellKnownName = "GridConnectionRollup1h",
            CkTypeId = "System.StreamData/RollupArchive", BlueprintId = "B-1.1.0", IsRollup = true,
            Outcome = "Activated", Backfill = "Queued", BackfillJobRtId = "6ae5b33b2a27b45f5568bee8"
        },
        new()
        {
            ArchiveRtId = "6a0e000000000000000a0007", CkTypeId = "System.StreamData/RawArchive",
            BlueprintId = "B-1.1.0", Outcome = "SkippedStreamDataDisabled", Reason = "Stream data is not enabled",
            Backfill = "NotApplicable"
        }
    ];

    [Fact]
    public async Task InstallBlueprint_SurfacesTheArchiveActivationsAndWarnings()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.1.0", false))
            .ReturnsAsync(new BlueprintApplyResultDto
            {
                Success = true,
                ApplicationMode = "Install",
                Warnings = ["Stream data is not enabled"],
                ArchiveActivations = [.. SampleActivations()]
            });

        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.1.0");

        result.IsSuccess.Should().BeTrue();
        result.Result!.ArchiveActivations.Should().HaveCount(2);
        result.Result.ArchiveActivations[0].BackfillJobRtId.Should().Be("6ae5b33b2a27b45f5568bee8");
        result.Message.Should().Contain("1 Activated").And.Contain("1 SkippedStreamDataDisabled")
            .And.Contain("GridConnectionRollup1h: job 6ae5b33b2a27b45f5568bee8")
            .And.Contain("1 warning(s)");
    }

    [Fact]
    public async Task InstallBlueprint_WithoutArchives_KeepsTheMessageUnchanged()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", false))
            .ReturnsAsync(new BlueprintApplyResultDto { Success = true, ApplicationMode = "Install" });

        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.0.0");

        result.Message.Should().NotContain("Archives:").And.NotContain("warning");
    }

    [Fact]
    public async Task UpdateBlueprint_SurfacesTheResultWithArchiveActivations()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintUpdateAsync(DefaultTenantId,
                It.Is<BlueprintUpdateRequestDto>(r => r.TargetVersion == "B-1.1.0")))
            .ReturnsAsync(new BlueprintUpdateResultDto
            {
                Success = true,
                EntitiesAdded = 4,
                Warnings = ["w1", "w2"],
                ArchiveActivations = [.. SampleActivations()]
            });

        var result = await BlueprintTools.UpdateBlueprint(MockServer.Object, "B-1.1.0", confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.Result!.EntitiesAdded.Should().Be(4);
        result.Result.ArchiveActivations.Should().HaveCount(2);
        result.Message.Should().Contain("1 Activated").And.Contain("job 6ae5b33b2a27b45f5568bee8")
            .And.Contain("2 warning(s)");
    }

    [Fact]
    public async Task InstallBlueprint_ForceTrue_PassesFlag()
    {
        MockAssetClient.Setup(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", true))
            .ReturnsAsync(new BlueprintApplyResultDto { Success = true, ApplicationMode = "Reapply" });

        await BlueprintTools.InstallBlueprint(MockServer.Object, "B-1.0.0", force: true);

        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(DefaultTenantId, "B-1.0.0", true), Times.Once);
    }

    [Fact]
    public async Task InstallBlueprint_MissingId_ReturnsValidationError()
    {
        var result = await BlueprintTools.InstallBlueprint(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetBlueprintHistory_HappyPath_ReturnsHistory()
    {
        MockAssetClient.Setup(c => c.GetBlueprintHistoryAsync(DefaultTenantId))
            .ReturnsAsync(new List<BlueprintHistoryItemDto>
            {
                new() { BlueprintId = "B-1.0.0", ApplicationMode = "Install", AppliedAt = DateTime.UtcNow }
            });

        var result = await BlueprintTools.GetBlueprintHistory(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.History.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetBlueprintUpdateInfo_WithUpdate_FormatsMessage()
    {
        MockAssetClient.Setup(c => c.GetBlueprintUpdateInfoAsync(DefaultTenantId))
            .ReturnsAsync(new BlueprintUpdateInfoDto
            {
                CurrentVersion = "1.0.0", RecommendedVersion = "2.0.0", HasUpdate = true
            });

        var result = await BlueprintTools.GetBlueprintUpdateInfo(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.UpdateInfo!.HasUpdate.Should().BeTrue();
        result.Message.Should().Contain("1.0.0").And.Contain("2.0.0");
    }

    [Fact]
    public async Task PreviewBlueprintUpdate_HappyPath_RunsDryRunRequest()
    {
        MockAssetClient.Setup(c => c.PreviewBlueprintUpdateAsync(DefaultTenantId,
                It.Is<BlueprintUpdateRequestDto>(r =>
                    r.TargetVersion == "B-2.0.0" && r.UpdateMode == "Safe" && r.DryRun == true)))
            .ReturnsAsync(new BlueprintUpdatePreviewDto { TargetVersion = "B-2.0.0", EntitiesToAdd = 5 });

        var result = await BlueprintTools.PreviewBlueprintUpdate(MockServer.Object, "B-2.0.0", "Safe");

        result.IsSuccess.Should().BeTrue();
        result.Preview!.EntitiesToAdd.Should().Be(5);
    }

    [Fact]
    public async Task UpdateBlueprint_WithoutConfirm_AndNotDryRun_Refuses()
    {
        var result = await BlueprintTools.UpdateBlueprint(MockServer.Object, "B-2.0.0");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockAssetClient.Verify(c => c.ApplyBlueprintUpdateAsync(
            It.IsAny<string>(), It.IsAny<BlueprintUpdateRequestDto>()), Times.Never);
    }

    [Fact]
    public async Task UpdateBlueprint_DryRunWithoutConfirm_AllowedAndCalledWithDryRunFlag()
    {
        var result = await BlueprintTools.UpdateBlueprint(MockServer.Object, "B-2.0.0", dryRun: true);

        result.IsSuccess.Should().BeTrue();
        result.DryRun.Should().BeTrue();
        MockAssetClient.Verify(c => c.ApplyBlueprintUpdateAsync(DefaultTenantId,
            It.Is<BlueprintUpdateRequestDto>(r => r.DryRun == true && r.TargetVersion == "B-2.0.0")), Times.Once);
    }

    [Fact]
    public async Task UpdateBlueprint_WithConfirm_CallsSdk()
    {
        var result = await BlueprintTools.UpdateBlueprint(MockServer.Object,
            "B-2.0.0", updateMode: "Full", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.ApplyBlueprintUpdateAsync(DefaultTenantId,
            It.Is<BlueprintUpdateRequestDto>(r =>
                r.TargetVersion == "B-2.0.0" && r.UpdateMode == "Full" &&
                r.DryRun == false)), Times.Once);
    }

    [Fact]
    public async Task ListBlueprintInstallations_HappyPath_ReturnsList()
    {
        MockAssetClient.Setup(c => c.ListBlueprintInstallationsAsync(DefaultTenantId))
            .ReturnsAsync(new List<BlueprintInstallationDto>
            {
                new() { BlueprintId = "B-1.0.0", InstalledAt = DateTime.UtcNow }
            });

        var result = await BlueprintTools.ListBlueprintInstallations(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Installations.Should().HaveCount(1);
    }

    [Fact]
    public async Task UninstallBlueprint_WithoutConfirm_Refuses()
    {
        var result = await BlueprintTools.UninstallBlueprint(MockServer.Object, "MyBlueprint");

        result.IsSuccess.Should().BeFalse();
        MockAssetClient.Verify(c => c.UninstallBlueprintAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task UninstallBlueprint_WithConfirm_CallsSdk()
    {
        MockAssetClient.Setup(c => c.UninstallBlueprintAsync(DefaultTenantId, "MyBlueprint", true))
            .ReturnsAsync(new BlueprintUninstallResultDto
            {
                Success = true,
                UninstalledBlueprintId = "MyBlueprint",
                EntitiesDeleted = 100,
                CascadedDependencies = ["dep-1"]
            });

        var result = await BlueprintTools.UninstallBlueprint(MockServer.Object,
            "MyBlueprint", cascade: true, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.Result!.EntitiesDeleted.Should().Be(100);
    }

    [Fact]
    public async Task UninstallBlueprint_BlockedByDependents_FormatsHelpfulMessage()
    {
        MockAssetClient.Setup(c => c.UninstallBlueprintAsync(DefaultTenantId, "MyBlueprint", false))
            .ReturnsAsync(new BlueprintUninstallResultDto
            {
                Success = false,
                BlockingDependents = ["DependentBp"]
            });

        var result = await BlueprintTools.UninstallBlueprint(MockServer.Object,
            "MyBlueprint", confirm: true);

        result.IsSuccess.Should().BeTrue();  // SDK call itself succeeded; message conveys the block.
        result.Message.Should().Contain("DependentBp").And.Contain("cascade=true");
    }
}
