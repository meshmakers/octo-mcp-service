using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>AB#8492: <c>validate_ck_model</c> validates an uploaded draft through the SDK and imports nothing.</summary>
public class CkModelValidationToolsTests : ToolTestBase
{
    public CkModelValidationToolsTests()
    {
        GivenAuthenticated();
    }

    private string StageUpload(string fileName = "draft.zip")
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, fileName);
        File.WriteAllText(path, "PK");
        FileTransferStore.CompleteUpload(id, 2);
        return id;
    }

    [Fact]
    public async Task ValidateCkModel_AnInvalidDraft_ReturnsTheErrors_AndKeepsTheUpload()
    {
        var transferId = StageUpload();
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync(new CkModelValidationResponseDto
            {
                IsValid = false,
                ModelId = "Demo-1.0.0",
                SourceKind = "Source",
                Errors =
                [
                    new CkModelValidationMessageDto
                    {
                        MessageNumber = 3, Level = "Error", File = "types/pump.yaml",
                        ElementPath = "Demo-1.0.0/Pump-1.Demo/NoSuchAttribute", Message = "unknown attribute"
                    }
                ]
            });

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeFalse();
        result.TenantId.Should().Be(DefaultTenantId);
        var error = result.Errors.Should().ContainSingle().Subject;
        error.File.Should().Be("types/pump.yaml");
        error.ElementPath.Should().Contain("Pump");
        // Nothing is imported, and the upload stays for a fix-and-revalidate loop or the import.
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        FileTransferStore.GetUpload(transferId, CallerSession).Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateCkModel_AValidDraft_ReportsValid_AndTheBlockers()
    {
        var transferId = StageUpload("draft.yaml");
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.Is<string>(p => p.EndsWith(".yaml"))))
            .ReturnsAsync(new CkModelValidationResponseDto
            {
                IsValid = true, ModelId = "Demo-2.0.0", SourceKind = "Compiled", IsBlocked = true,
                Blockers = [new CkUpgradeBlockerDto { Dependent = "Other-1.0.0", Update = "Demo-2.0.0" }]
            });

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeTrue();
        result.IsBlocked.Should().BeTrue();
        result.Blockers.Should().ContainSingle().Which.Dependent.Should().Be("Other-1.0.0");
        result.Message.Should().Contain("would break 1");
    }

    [Fact]
    public async Task ValidateCkModel_Unauthenticated_ReturnsError_AndCallsNothing()
    {
        var transferId = StageUpload();
        GivenUnauthenticated();

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_MissingTransferId_ReturnsValidationError()
    {
        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, " ");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("transferId");
    }

    [Fact]
    public async Task ValidateCkModel_UnknownTransferId_ReturnsError_AndCallsNothing()
    {
        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, "nope");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_AServiceError_IsAnErrorResponse_NotAnException()
    {
        var transferId = StageUpload();
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("asset repository unreachable"));

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unreachable");
    }

    [Fact]
    public void ValidateCkModel_IsLowRisk()
    {
        new ToolRiskRegistry(typeof(CkModelValidationTools).Assembly).GetRiskLevel("validate_ck_model")
            .Should().Be(McpRiskLevel.Low);
    }
}
