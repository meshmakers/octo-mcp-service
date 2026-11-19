using System.IO.Compression;
using System.Runtime.CompilerServices;
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
        var (id, path) = FileTransferStore.ReserveUpload(CallerOwner, fileName);
        File.WriteAllText(path, "PK");
        CompleteStagedUpload(id, 2);
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

    // ---- AB#9005: inline drafts for MCP-only clients ----

    private static List<CkDraftFileDto> SourceDraft() =>
    [
        new() { Name = "ckModel.yaml", Content = "modelId: Demo-1.0.0\n" },
        new() { Name = "types/pump.yaml", Content = "types:\n  - typeId: Pump\n" }
    ];

    [Fact]
    public async Task ValidateCkModel_InlineFiles_Valid_PostsAZipOfTheFiles_AndDeletesItAfterwards()
    {
        string? postedPath = null;
        List<string>? entries = null;
        string? pumpContent = null;
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) =>
            {
                postedPath = path;
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Select(e => e.FullName).ToList();
                using var reader = new StreamReader(zip.GetEntry("types/pump.yaml")!.Open());
                pumpContent = reader.ReadToEnd();
            })
            .ReturnsAsync(new CkModelValidationResponseDto { IsValid = true, ModelId = "Demo-1.0.0", SourceKind = "Source" });

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeTrue();
        result.SourceKind.Should().Be("Source");
        result.ModelId.Should().Be("Demo-1.0.0");
        entries.Should().BeEquivalentTo("ckModel.yaml", "types/pump.yaml");
        pumpContent.Should().Contain("typeId: Pump");
        postedPath.Should().EndWith(".zip");
        File.Exists(postedPath).Should().BeFalse("an inline draft lives only for the call");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_InlineFiles_Invalid_ReturnsTheSameErrorContract()
    {
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync(new CkModelValidationResponseDto
            {
                IsValid = false, ModelId = "Demo-1.0.0", SourceKind = "Source",
                Errors =
                [
                    new CkModelValidationMessageDto
                    {
                        MessageNumber = 3, Level = "Error", File = "types/pump.yaml",
                        ElementPath = "Demo-1.0.0/Pump-1.Demo/NoSuchAttribute", Message = "unknown attribute"
                    }
                ]
            });

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeFalse();
        result.Message.Should().Contain("1 error");
        var error = result.Errors.Should().ContainSingle().Subject;
        error.MessageNumber.Should().Be(3);
        error.File.Should().Be("types/pump.yaml");
    }

    [Fact]
    public async Task ValidateCkModel_InlineFilesOverTheLimit_AreRefusedWithTheMessageNumber_AndCallNothing()
    {
        var files = new List<CkDraftFileDto>
        {
            new() { Name = "ckModel.yaml", Content = "modelId: Demo-1.0.0" },
            new() { Name = "types/big.yaml", Content = new string('x', CkDraftInlinePackager.MaxInlineBytes) }
        };

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: files);

        result.IsSuccess.Should().BeFalse();
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("InlineDraftTooLarge");
        result.ErrorMessage.Should().Contain("prepare_file_upload").And.Contain("9005");
        result.Errors.Should().ContainSingle().Which.MessageNumber.Should().Be(9005);
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_TooManyInlineFiles_AreRefused()
    {
        var files = Enumerable.Range(0, CkDraftInlinePackager.MaxInlineFiles + 1)
            .Select(i => new CkDraftFileDto { Name = $"types/t{i}.yaml", Content = "x" }).ToList();

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: files);

        result.ErrorCode.Should().Be("InlineDraftTooLarge");
    }

    [Theory]
    [InlineData("../ckModel.yaml")]
    [InlineData("/etc/passwd")]
    [InlineData("types/../../x.yaml")]
    [InlineData("C:\\x.yaml")]
    [InlineData(" ")]
    public async Task ValidateCkModel_UnsafeInlineFileName_IsRejected_AndCallsNothing(string name)
    {
        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = name, Content = "x" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().Contain("Invalid file name");
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_DuplicateInlineFileName_IsRejected()
    {
        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "types/a.yaml" }, new CkDraftFileDto { Name = "types//A.yaml" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("given more than once");
    }

    [Fact]
    public async Task ValidateCkModel_TwoDraftSources_AreRejected()
    {
        var transferId = StageUpload();

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, transferId, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exactly one");
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_InlineDraft_AServiceError_StillDeletesTheTempZip()
    {
        string? postedPath = null;
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) => postedPath = path)
            .ThrowsAsync(new InvalidOperationException("asset repository unreachable"));

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        postedPath.Should().NotBeNull();
        File.Exists(postedPath).Should().BeFalse();
    }

    [Fact]
    public async Task ValidateCkModel_InlineDraft_Unauthenticated_CallsNothing_AndLeavesNoTempZip()
    {
        GivenUnauthenticated();
        var before = Directory.GetFiles(Path.GetTempPath(), "octo-mcp-ck-inline-*.zip").ToHashSet();

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: SourceDraft());

        result.ErrorMessage.Should().Contain("Not authenticated");
        Directory.GetFiles(Path.GetTempPath(), "octo-mcp-ck-inline-*.zip").Where(f => !before.Contains(f))
            .Should().BeEmpty();
        MockAssetClient.Verify(c => c.ValidateCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCkModel_RepeatedInlineCalls_DoNotRetainTheDraft()
    {
        // AB#9015 guard: the inline draft is per-call state. 200 calls with a ~0.5 MB draft must not keep the drafts
        // (strings, zips) reachable or leave temp files behind.
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync(new CkModelValidationResponseDto { IsValid = true });
        var before = Directory.GetFiles(Path.GetTempPath(), "octo-mcp-ck-inline-*.zip").Length;

        var weak = await CallAndForget();
        for (var i = 0; i < 199; i++)
        {
            await CkModelValidationTools.ValidateCkModel(MockServer.Object,
                files: [new CkDraftFileDto { Name = "ckModel.yaml", Content = new string('z', 512 * 1024) }]);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);

        weak.IsAlive.Should().BeFalse("the tool must not keep the inline draft after the call");
        Directory.GetFiles(Path.GetTempPath(), "octo-mcp-ck-inline-*.zip").Length.Should().BeLessThanOrEqualTo(before);
    }

    [Fact]
    public async Task ValidateCkModel_InlineSingleCompiledModelFile_IsPostedAsAOneEntryZip()
    {
        List<string>? entries = null;
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) =>
            {
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Select(e => e.FullName).ToList();
            })
            .ReturnsAsync(new CkModelValidationResponseDto { IsValid = true, SourceKind = "Compiled" });

        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "ck-demo-1.yaml", Content = "modelId: Demo-1.0.0" }]);

        result.SourceKind.Should().Be("Compiled");
        entries.Should().Equal("ck-demo-1.yaml");
    }

    [Fact]
    public async Task ValidateCkModel_InlineContentAtTheLimit_IsAccepted_CountedInUtf8Bytes()
    {
        MockAssetClient.Setup(c => c.ValidateCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync(new CkModelValidationResponseDto { IsValid = true });
        var atLimit = new string('x', CkDraftInlinePackager.MaxInlineBytes);
        // 'ä' is two UTF-8 bytes: half the limit in characters is exactly the limit in bytes, one more is over it.
        var umlautsAtLimit = new string('ä', CkDraftInlinePackager.MaxInlineBytes / 2);

        (await CkModelValidationTools.ValidateCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "a.yaml", Content = atLimit }])).IsSuccess.Should().BeTrue();
        (await CkModelValidationTools.ValidateCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "a.yaml", Content = umlautsAtLimit }])).IsSuccess.Should().BeTrue();
        (await CkModelValidationTools.ValidateCkModel(MockServer.Object,
                files: [new CkDraftFileDto { Name = "a.yaml", Content = umlautsAtLimit + "ä" }])).ErrorCode
            .Should().Be("InlineDraftTooLarge");
    }

    [Fact]
    public async Task ValidateCkModel_EmptyFilesAndNoOtherSource_IsRejected()
    {
        var result = await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: []);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("transferId is required");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> CallAndForget()
    {
        var files = new List<CkDraftFileDto> { new() { Name = "ckModel.yaml", Content = new string('y', 512 * 1024) } };
        var weak = new WeakReference(files);
        await CkModelValidationTools.ValidateCkModel(MockServer.Object, files: files);
        return weak;
    }
}
