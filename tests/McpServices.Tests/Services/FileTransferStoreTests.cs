using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

public class FileTransferStoreTests
{
    [Fact]
    public void ReserveUpload_ReturnsIdAndTargetPath()
    {
        var store = new FileTransferStore();

        var (id, path) = store.ReserveUpload("sess-1", "model.json");

        id.Should().NotBeNullOrEmpty();
        path.Should().NotBeNullOrEmpty();
        path.Should().Contain("model.json");
    }

    [Fact]
    public void CompleteUpload_MakesUploadVisible()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "model.json");

        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);

        var upload = store.GetUpload(id, "sess-1");
        upload.Should().NotBeNull();
        upload!.SessionId.Should().Be("sess-1");
        upload.SizeBytes.Should().Be(2);
        upload.FileName.Should().Be("model.json");
    }

    [Fact]
    public void GetUpload_BeforeComplete_ReturnsNull()
    {
        var store = new FileTransferStore();
        var (id, _) = store.ReserveUpload("sess-1", "model.json");

        store.GetUpload(id, "sess-1").Should().BeNull();
    }

    [Fact]
    public void GetUpload_UnknownId_ReturnsNull()
    {
        var store = new FileTransferStore();
        store.GetUpload("nope", "sess-1").Should().BeNull();
    }

    [Fact]
    public void DeleteUpload_RemovesEntryAndFile()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "model.json");
        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);

        store.DeleteUpload(id);

        store.GetUpload(id, "sess-1").Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void RegisterDownload_AndGetDownload_RoundtripsMetadata()
    {
        var store = new FileTransferStore();
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "dump-content");

        var id = store.RegisterDownload("sess-1", path, "dump.tar.gz");

        var download = store.GetDownload(id);
        download.Should().NotBeNull();
        download!.FileName.Should().Be("dump.tar.gz");
        download.FilePath.Should().Be(path);
        download.SizeBytes.Should().BeGreaterThan(0);

        store.DeleteDownload(id);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void ExpiredUpload_ReturnsNullAndDeletes()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "x.json", TimeSpan.FromMilliseconds(1));
        File.WriteAllText(path, "x");
        store.CompleteUpload(id, 1);

        Thread.Sleep(50);

        store.GetUpload(id, "sess-1").Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void TryReserveTarget_ReturnsTargetForActiveReservation()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "x.json");

        store.TryReserveTarget(id).Should().Be(path);
    }

    [Fact]
    public void TryReserveTarget_UnknownId_ReturnsNull()
    {
        var store = new FileTransferStore();
        store.TryReserveTarget("nope").Should().BeNull();
    }

    // ── AB#6989: ownership ──────────────────────────────────────────────────

    [Fact]
    public void GetUpload_ForeignSession_ReturnsNullAndKeepsUploadForOwner()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "model.json");
        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);

        store.GetUpload(id, "sess-2").Should().BeNull();

        store.GetUpload(id, "sess-1").Should().NotBeNull("the foreign attempt must not consume the upload");
        File.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("anonymous")]
    public void GetUpload_UnidentifiableCaller_NeverMatches(string caller)
    {
        var store = new FileTransferStore();
        // Even an upload that was itself tagged "anonymous" must not be consumable: the label is shared.
        var (id, path) = store.ReserveUpload("anonymous", "model.json");
        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);

        store.GetUpload(id, caller).Should().BeNull();
    }

    [Fact]
    public void CancelTransfer_ForeignSession_IsNoOp()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "model.json");
        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);
        var downloadFile = Path.GetTempFileName();
        var downloadId = store.RegisterDownload("sess-1", downloadFile, "dump.tar.gz");

        store.CancelTransfer(id, "sess-2");
        store.CancelTransfer(downloadId, "sess-2");

        store.GetUpload(id, "sess-1").Should().NotBeNull();
        store.GetDownload(downloadId).Should().NotBeNull();
        File.Exists(downloadFile).Should().BeTrue();
    }

    [Fact]
    public void CancelTransfer_OwnSession_RemovesUploadPendingSlotAndDownload()
    {
        var store = new FileTransferStore();
        var (doneId, donePath) = store.ReserveUpload("sess-1", "a.json");
        File.WriteAllText(donePath, "{}");
        store.CompleteUpload(doneId, 2);
        var (pendingId, _) = store.ReserveUpload("sess-1", "b.json");
        var downloadFile = Path.GetTempFileName();
        var downloadId = store.RegisterDownload("sess-1", downloadFile, "dump.tar.gz");

        store.CancelTransfer(doneId, "sess-1");
        store.CancelTransfer(pendingId, "sess-1");
        store.CancelTransfer(downloadId, "sess-1");

        store.GetUpload(doneId, "sess-1").Should().BeNull();
        File.Exists(donePath).Should().BeFalse();
        store.TryReserveTarget(pendingId).Should().BeNull();
        store.GetDownload(downloadId).Should().BeNull();
        File.Exists(downloadFile).Should().BeFalse();
    }

    [Fact]
    public void GetDownload_Expired_ReturnsNullAndDeletesFile()
    {
        var store = new FileTransferStore();
        var path = Path.GetTempFileName();
        var id = store.RegisterDownload("sess-1", path, "dump.tar.gz", TimeSpan.FromMilliseconds(1));

        Thread.Sleep(50);

        store.GetDownload(id).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void SweepExpired_RemovesOnlyExpiredDownloads()
    {
        var store = new FileTransferStore();
        var expiredPath = Path.GetTempFileName();
        var livePath = Path.GetTempFileName();
        var expired = store.RegisterDownload("sess-1", expiredPath, "a", TimeSpan.FromMilliseconds(1));
        var live = store.RegisterDownload("sess-1", livePath, "b");
        Thread.Sleep(50);

        store.SweepExpired();

        File.Exists(expiredPath).Should().BeFalse();
        File.Exists(livePath).Should().BeTrue();
        store.GetDownload(expired).Should().BeNull();
        store.GetDownload(live).Should().NotBeNull();
        store.DeleteDownload(live);
    }
}
