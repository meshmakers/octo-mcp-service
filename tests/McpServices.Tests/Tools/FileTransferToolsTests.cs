using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class FileTransferToolsTests : ToolTestBase
{
    public FileTransferToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task PrepareFileUpload_HappyPath_ReturnsTransferIdAndUrl()
    {
        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");

        result.IsSuccess.Should().BeTrue();
        result.TransferId.Should().NotBeNullOrEmpty();
        result.UploadUrlPath.Should().StartWith("/file-transfer/upload/");
        result.UploadUrlPath.Should().Contain(result.TransferId!);
    }

    [Fact]
    public async Task PrepareFileUpload_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task PrepareFileUpload_MissingFileName_ReturnsValidationError()
    {
        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("fileName");
    }

    [Fact]
    public async Task CancelFileTransfer_RemovesEntries()
    {
        var prep = await FileTransferTools.PrepareFileUpload(MockServer.Object, "x.json");
        prep.IsSuccess.Should().BeTrue();

        // Simulate the upload completing in the store.
        var target = FileTransferStore.TryReserveTarget(prep.TransferId!)!;
        File.WriteAllText(target, "x");
        FileTransferStore.CompleteUpload(prep.TransferId!, 1);

        var result = await FileTransferTools.CancelFileTransfer(MockServer.Object, prep.TransferId!);

        result.IsSuccess.Should().BeTrue();
        FileTransferStore.GetUpload(prep.TransferId!).Should().BeNull();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CancelFileTransfer_MissingId_ReturnsValidationError()
    {
        var result = await FileTransferTools.CancelFileTransfer(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task RunFixupScripts_WithoutConfirm_Refuses()
    {
        var result = await FileTransferTools.RunFixupScripts(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        MockBotClient.Verify(c => c.StartRunFixupScriptAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RunFixupScripts_WithConfirm_StartsJobAndReturnsJobId()
    {
        MockBotClient.Setup(c => c.StartRunFixupScriptAsync(DefaultTenantId))
            .ReturnsAsync(new JobResponseDto("job-42"));

        var result = await FileTransferTools.RunFixupScripts(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().Be("job-42");
    }
}
