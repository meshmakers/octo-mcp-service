using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

public class FileTransferStoreTests
{
    private const string Sess1Principal = "c:sess-1";

    /// <summary>Owner tag whose principal follows the label (the anonymous label has no principal).</summary>
    private static FileTransferOwner O(string label) =>
        new(label, label == McpSessionContext.AnonymousCallerLabel ? null : "c:" + label);

    /// <summary>What a successful PUT does: claim, then commit.</summary>
    private static void Complete(FileTransferStore store, string id, long size, string principal = Sess1Principal)
    {
        store.TryClaimUpload(id, principal).Status.Should().Be(UploadClaimStatus.Claimed);
        store.CompleteUpload(id, size).Should().BeTrue();
    }

    [Fact]
    public void ReserveUpload_ReturnsIdAndTargetPath()
    {
        var store = new FileTransferStore();

        var (id, path) = store.ReserveUpload(O("sess-1"), "model.json");

        id.Should().NotBeNullOrEmpty();
        path.Should().NotBeNullOrEmpty();
        path.Should().Contain("model.json");
    }

    [Fact]
    public void CompleteUpload_MakesUploadVisible()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "model.json");

        File.WriteAllText(path, "{}");
        Complete(store, id, 2);

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
        var (id, _) = store.ReserveUpload(O("sess-1"), "model.json");

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
        var (id, path) = store.ReserveUpload(O("sess-1"), "model.json");
        File.WriteAllText(path, "{}");
        Complete(store, id, 2);

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

        var id = store.RegisterDownload(O("sess-1"), path, "dump.tar.gz");

        var download = store.GetDownload(id, Sess1Principal);
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
        var (id, path) = store.ReserveUpload(O("sess-1"), "x.json", TimeSpan.FromMilliseconds(200));
        File.WriteAllText(path, "x");
        Complete(store, id, 1);
        Thread.Sleep(250);

        Thread.Sleep(50);

        store.GetUpload(id, "sess-1").Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void TryClaimUpload_ByTheOwner_ReturnsTheTarget_ASecondClaimIsInProgress()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "x.json");

        store.TryClaimUpload(id, Sess1Principal).Should().Be((UploadClaimStatus.Claimed, path));
        store.TryClaimUpload(id, Sess1Principal).Should().Be((UploadClaimStatus.InProgress, (string?)null));
    }

    [Fact]
    public void TryClaimUpload_UnknownId_ForeignOrMissingPrincipal_IsNotFound_AndLeavesTheSlot()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "x.json");

        store.TryClaimUpload("nope", Sess1Principal).Status.Should().Be(UploadClaimStatus.NotFound);
        store.TryClaimUpload(id, "c:sess-2").Status.Should().Be(UploadClaimStatus.NotFound);
        store.TryClaimUpload(id, null).Status.Should().Be(UploadClaimStatus.NotFound);
        store.TryClaimUpload(id, McpSessionContext.AnonymousCallerLabel).Status.Should().Be(UploadClaimStatus.NotFound);
        store.TryClaimUpload(id, Sess1Principal).Should().Be((UploadClaimStatus.Claimed, path));
    }

    [Fact]
    public void TryClaimUpload_ForeignPrincipal_GetsNotFound_EvenWhileTheOwnersPutRuns()
    {
        // 409 would tell a foreign caller that the id exists.
        var store = new FileTransferStore();
        var (id, _) = store.ReserveUpload(O("sess-1"), "x.json");
        store.TryClaimUpload(id, Sess1Principal);

        store.TryClaimUpload(id, "c:sess-2").Status.Should().Be(UploadClaimStatus.NotFound);
    }

    [Fact]
    public void TryClaimUpload_ManyConcurrentClaims_ExactlyOneWins()
    {
        var store = new FileTransferStore();
        for (var round = 0; round < 50; round++)
        {
            var (id, _) = store.ReserveUpload(O("sess-1"), "x.json");
            var results = new UploadClaimStatus[16];
            using var start = new Barrier(results.Length);
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i =>
            {
                start.SignalAndWait();
                results[i] = store.TryClaimUpload(id, Sess1Principal).Status;
            });

            results.Count(r => r == UploadClaimStatus.Claimed).Should().Be(1);
            results.Count(r => r == UploadClaimStatus.InProgress).Should().Be(results.Length - 1);
        }
    }

    [Fact]
    public void CompleteUpload_WithoutClaim_IsRefused()
    {
        var store = new FileTransferStore();
        var (id, _) = store.ReserveUpload(O("sess-1"), "x.json");

        store.CompleteUpload(id, 1).Should().BeFalse();
        store.GetUpload(id, "sess-1").Should().BeNull();
    }

    [Fact]
    public void AbortUpload_EndsTheReservationAndDeletesThePartialFile()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "x.json");
        store.TryClaimUpload(id, Sess1Principal);
        File.WriteAllText(path, "half");

        store.AbortUpload(id);

        File.Exists(path).Should().BeFalse();
        store.TryClaimUpload(id, Sess1Principal).Status.Should().Be(UploadClaimStatus.NotFound);
        store.CompleteUpload(id, 4).Should().BeFalse();
    }

    [Fact]
    public void ExpiredReservation_CannotBeClaimed()
    {
        var store = new FileTransferStore();
        var (id, _) = store.ReserveUpload(O("sess-1"), "x.json", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(30);

        store.TryClaimUpload(id, Sess1Principal).Status.Should().Be(UploadClaimStatus.NotFound);
    }

    [Fact]
    public void GetDownload_ForeignOrMissingPrincipal_ReturnsNull_AndKeepsTheEntry()
    {
        var store = new FileTransferStore();
        var path = Path.GetTempFileName();
        var id = store.RegisterDownload(O("sess-1"), path, "dump.tar.gz");

        store.GetDownload(id, "c:sess-2").Should().BeNull();
        store.GetDownload(id, null).Should().BeNull();
        store.GetDownload(id, Sess1Principal).Should().NotBeNull();
        store.DeleteDownload(id);
    }

    [Fact]
    public void Entries_OfAnUnidentifiableCreator_MatchNoPrincipal()
    {
        var store = new FileTransferStore();
        var (uploadId, _) = store.ReserveUpload(O("anonymous"), "x.json");
        var path = Path.GetTempFileName();
        var downloadId = store.RegisterDownload(new FileTransferOwner("anonymous", McpSessionContext.AnonymousCallerLabel), path, "d");

        store.TryClaimUpload(uploadId, McpSessionContext.AnonymousCallerLabel).Status.Should().Be(UploadClaimStatus.NotFound);
        store.GetDownload(downloadId, McpSessionContext.AnonymousCallerLabel).Should().BeNull();
        store.DeleteDownload(downloadId);
    }

    // ── ownership ──────────────────────────────────────────────────

    [Fact]
    public void GetUpload_ForeignSession_ReturnsNullAndKeepsUploadForOwner()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "model.json");
        File.WriteAllText(path, "{}");
        Complete(store, id, 2);

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
        var (id, path) = store.ReserveUpload(new FileTransferOwner("anonymous", "c:anon-owner"), "model.json");
        File.WriteAllText(path, "{}");
        Complete(store, id, 2, "c:anon-owner");

        store.GetUpload(id, caller).Should().BeNull();
    }

    [Fact]
    public void CancelTransfer_ForeignSession_IsNoOp()
    {
        var store = new FileTransferStore();
        var (id, path) = store.ReserveUpload(O("sess-1"), "model.json");
        File.WriteAllText(path, "{}");
        Complete(store, id, 2);
        var downloadFile = Path.GetTempFileName();
        var downloadId = store.RegisterDownload(O("sess-1"), downloadFile, "dump.tar.gz");

        store.CancelTransfer(id, "sess-2");
        store.CancelTransfer(downloadId, "sess-2");

        store.GetUpload(id, "sess-1").Should().NotBeNull();
        store.GetDownload(downloadId, Sess1Principal).Should().NotBeNull();
        File.Exists(downloadFile).Should().BeTrue();
    }

    [Fact]
    public void CancelTransfer_OwnSession_RemovesUploadPendingSlotAndDownload()
    {
        var store = new FileTransferStore();
        var (doneId, donePath) = store.ReserveUpload(O("sess-1"), "a.json");
        File.WriteAllText(donePath, "{}");
        Complete(store, doneId, 2);
        var (pendingId, _) = store.ReserveUpload(O("sess-1"), "b.json");
        var downloadFile = Path.GetTempFileName();
        var downloadId = store.RegisterDownload(O("sess-1"), downloadFile, "dump.tar.gz");

        store.CancelTransfer(doneId, "sess-1");
        store.CancelTransfer(pendingId, "sess-1");
        store.CancelTransfer(downloadId, "sess-1");

        store.GetUpload(doneId, "sess-1").Should().BeNull();
        File.Exists(donePath).Should().BeFalse();
        store.TryClaimUpload(pendingId, Sess1Principal).Status.Should().Be(UploadClaimStatus.NotFound);
        store.GetDownload(downloadId, Sess1Principal).Should().BeNull();
        File.Exists(downloadFile).Should().BeFalse();
    }

    [Fact]
    public void GetDownload_Expired_ReturnsNullAndDeletesFile()
    {
        var store = new FileTransferStore();
        var path = Path.GetTempFileName();
        var id = store.RegisterDownload(O("sess-1"), path, "dump.tar.gz", TimeSpan.FromMilliseconds(1));

        Thread.Sleep(50);

        store.GetDownload(id, Sess1Principal).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void SweepExpired_RemovesOnlyExpiredDownloads()
    {
        var store = new FileTransferStore();
        var expiredPath = Path.GetTempFileName();
        var livePath = Path.GetTempFileName();
        var expired = store.RegisterDownload(O("sess-1"), expiredPath, "a", TimeSpan.FromMilliseconds(1));
        var live = store.RegisterDownload(O("sess-1"), livePath, "b");
        Thread.Sleep(50);

        store.SweepExpired();

        File.Exists(expiredPath).Should().BeFalse();
        File.Exists(livePath).Should().BeTrue();
        store.GetDownload(expired, Sess1Principal).Should().BeNull();
        store.GetDownload(live, Sess1Principal).Should().NotBeNull();
        store.DeleteDownload(live);
    }
}
