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

        var upload = store.GetUpload(id);
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

        store.GetUpload(id).Should().BeNull();
    }

    [Fact]
    public void GetUpload_UnknownId_ReturnsNull()
    {
        var store = new FileTransferStore();
        store.GetUpload("nope").Should().BeNull();
    }

    [Fact]
    public void DeleteUpload_RemovesEntryAndFile()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload("sess-1", "model.json");
        File.WriteAllText(path, "{}");
        store.CompleteUpload(id, 2);

        store.DeleteUpload(id);

        store.GetUpload(id).Should().BeNull();
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

        store.GetUpload(id).Should().BeNull();
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
}
