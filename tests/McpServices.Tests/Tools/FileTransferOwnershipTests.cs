using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#6989: an upload can only be consumed — and a transfer only cancelled — by the session that created
///     it. A foreign caller is answered exactly like an unknown id and the owner's file stays untouched.
/// </summary>
public class FileTransferOwnershipTests : ToolTestBase
{
    private const string OtherSession = "someone-else|their-mcp-session";

    public FileTransferOwnershipTests()
    {
        GivenAuthenticated();
    }

    /// <summary>An uploaded file that belongs to a different session than the current test caller.</summary>
    private (string TransferId, string Path) StageForeignUpload()
    {
        CallerSession.Should().NotBe(OtherSession);
        var (id, path) = FileTransferStore.ReserveUpload(OtherSession, "foreign.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        FileTransferStore.CompleteUpload(id, 3);
        return (id, path);
    }

    private void AssertUntouchedForOwner(string transferId, string path)
    {
        FileTransferStore.GetUpload(transferId, OtherSession).Should().NotBeNull(
            "a foreign attempt must neither consume nor delete the owner's upload");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ImportCkModel_ForeignUpload_FailsAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task ImportRuntimeModel_ForeignUpload_FailsAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task RestoreTenant_ForeignUpload_FailsAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var result = await TenantBackupTools.RestoreTenant(MockServer.Object, id, "target", "newdb",
            confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        MockBotClient.Verify(c => c.RestoreRepositoryWithTusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task ImportArchiveData_ForeignUpload_FailsAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task UploadFile_ForeignUpload_FailsAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var result = await PlatformFileTools.UploadFile(MockServer.Object, id, "Files", "a.bin");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task ForeignUpload_AnswersLikeUnknownId()
    {
        var (foreignId, _) = StageForeignUpload();

        var foreign = await CkModelFileTools.ImportCkModel(MockServer.Object, foreignId);
        var unknown = await CkModelFileTools.ImportCkModel(MockServer.Object, "0123456789abcdef0123456789abcdef");

        foreign.ErrorMessage!.Replace(foreignId, "<id>")
            .Should().Be(unknown.ErrorMessage!.Replace("0123456789abcdef0123456789abcdef", "<id>"));
    }

    [Fact]
    public async Task CancelFileTransfer_ForeignId_IsNoOpWithSameAnswerAsUnknown()
    {
        var (id, path) = StageForeignUpload();

        var foreign = await FileTransferTools.CancelFileTransfer(MockServer.Object, id);
        var unknown = await FileTransferTools.CancelFileTransfer(MockServer.Object, "nope");

        foreign.IsSuccess.Should().Be(unknown.IsSuccess);
        AssertUntouchedForOwner(id, path);
    }

    [Fact]
    public async Task OwnSession_PrepareThenImport_StillWorks()
    {
        var prep = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");
        prep.IsSuccess.Should().BeTrue();
        var target = FileTransferStore.TryReserveTarget(prep.TransferId!)!;
        File.WriteAllText(target, "{}");
        FileTransferStore.CompleteUpload(prep.TransferId!, 2);
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync("job-1");
        MockBotClient.Setup(c => c.GetImportJobStatus("job-1"))
            .ReturnsAsync(new JobDto { Id = "job-1", Status = "Succeeded" });

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, prep.TransferId!);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task SecondCallerOnSamePod_CannotConsumeFirstCallersUpload()
    {
        // Two real, authenticated principals — the session label is derived from the principal.
        var prep = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");
        var target = FileTransferStore.TryReserveTarget(prep.TransferId!)!;
        File.WriteAllText(target, "{}");
        FileTransferStore.CompleteUpload(prep.TransferId!, 2);

        GivenAuthenticatedCaller(subjectId: "another-user");
        GivenAuthenticated();
        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, prep.TransferId!);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found or expired");
        File.Exists(target).Should().BeTrue();
    }
}
