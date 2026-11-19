using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
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
        result.UploadUrlPath.Should().Be("/file-transfer/upload", "the transfer id never travels in the URL");
        result.TransferIdHeader.Should().Be("X-Transfer-Id");
        result.Message.Should().Contain("X-Transfer-Id: " + result.TransferId).And.Contain("Authorization: Bearer");
    }

    [Fact]
    public async Task PrepareFileUpload_ReturnsAOneTimeKey_ThatUnlocksTheSlotOnce_AndIsNotInTheMessage()
    {
        var before = DateTime.UtcNow;

        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");

        result.IsSuccess.Should().BeTrue();
        result.TransferKey.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        result.TransferKeyHeader.Should().Be("X-Transfer-Key");
        result.TransferKeyExpiresAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddMinutes(5));
        result.Message.Should().Contain("X-Transfer-Key").And.NotContain(result.TransferKey!);

        FileTransferStore.TryClaimUploadWithKey(result.TransferId!, result.TransferKey!, TransferKeyCaller.Anonymous)
            .Status.Should().Be(UploadClaimStatus.Claimed);
        FileTransferStore.HasTransferKey(result.TransferId!).Should().BeFalse("the key is spent");
    }

    [Fact]
    public async Task PrepareFileUpload_ClientCredentialsTokenWithoutTenant_IsRefused_NothingReserved()
    {
        // A client-credentials token without tenant_id can own no transfer (fail closed).
        GivenServicePrincipalCaller(tenantId: null);
        CallerOwner.CanOwnTransfer.Should().BeFalse();

        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("tenant_id");
        result.TransferId.Should().BeNull();
        result.TransferKey.Should().BeNull();
    }

    [Fact]
    public async Task PrepareFileUpload_ClientCredentialsTokenWithTenant_GetsAKey()
    {
        GivenServicePrincipalCaller(tenantId: DefaultTestTenantId);

        var result = await FileTransferTools.PrepareFileUpload(MockServer.Object, "model.json");

        result.IsSuccess.Should().BeTrue();
        result.TransferKey.Should().NotBeNullOrEmpty();
        CallerPrincipal.Should().Be($"c:octo-ai-worker:{DefaultTestTenantId}");
    }

    [Fact]
    public void RegisterDownload_ForATenantlessClient_IsRefused_AndTheFileIsDeleted()
    {
        GivenServicePrincipalCaller(tenantId: null);
        var path = Path.GetTempFileName();

        var handout = FileTransferHandout.RegisterDownload(MockServer.Object, FileTransferStore, path, "f.bin");

        handout.Should().BeNull();
        File.Exists(path).Should().BeFalse();
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
        var target = ClaimStagedUpload(prep.TransferId!);
        File.WriteAllText(target, "x");
        FileTransferStore.CompleteUpload(prep.TransferId!, 1).Should().BeTrue();

        var result = await FileTransferTools.CancelFileTransfer(MockServer.Object, prep.TransferId!);

        result.IsSuccess.Should().BeTrue();
        FileTransferStore.GetUpload(prep.TransferId!, CallerOwner).Should().BeNull();
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
