using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Files;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class PlatformFileToolsTests : ToolTestBase
{
    private const string FileRtId = "507f1f77bcf86cd799439011";
    private readonly Mock<IFilesClient> _files = new();

    public PlatformFileToolsTests()
    {
        GivenAuthenticated();
        MockAssetClient.Setup(c => c.Files).Returns(_files.Object);
    }

    private static FileEntryDto Entry(string name = "a.txt", string kind = FileEntryKinds.File) => new()
    {
        RtId = FileRtId, Name = name, Kind = kind, Root = "Files", Path = name, CkTypeId = "System.Files/FileSystemItem"
    };

    private string StageUpload(string content = "hello")
    {
        var (id, path) = FileTransferStore.ReserveUpload("test-session", "a.txt");
        File.WriteAllText(path, content);
        FileTransferStore.CompleteUpload(id, content.Length);
        return id;
    }

    // ── list_files ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_WithoutRoot_ListsRoots()
    {
        _files.Setup(f => f.ListRootsAsync(100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilesPage { Items = [Entry("Files", FileEntryKinds.Root)], TotalCount = 1 });

        var result = await PlatformFileTools.ListFiles(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        result.TenantId.Should().Be(DefaultTenantId);
    }

    [Fact]
    public async Task ListFiles_WithRootAndPath_ListsChildrenWithNormalizedPath()
    {
        _files.Setup(f => f.ListChildrenAsync("Files", "docs/2026", 50, "c1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilesPage { Items = [Entry()], TotalCount = 7, HasNextPage = true, EndCursor = "c2" });

        var result = await PlatformFileTools.ListFiles(MockServer.Object, "Files", "/docs/2026/", 50, "c1");

        result.IsSuccess.Should().BeTrue();
        result.TotalCount.Should().Be(7);
        result.EndCursor.Should().Be("c2");
        result.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task ListFiles_InvalidPageSize_ReturnsErrorWithoutSdkCall()
    {
        var result = await PlatformFileTools.ListFiles(MockServer.Object, "Files", first: 0);

        result.IsSuccess.Should().BeFalse();
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListFiles_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticated();

        var result = await PlatformFileTools.ListFiles(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListFiles_ApiError_CarriesErrorCode()
    {
        _files.Setup(f => f.ListChildrenAsync("nope", null, 100, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FilesApiException(FileErrorCodes.RootNotFound, System.Net.HttpStatusCode.NotFound, "x"));

        var result = await PlatformFileTools.ListFiles(MockServer.Object, "nope");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("ROOT_NOT_FOUND");
    }

    // ── upload_file ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadFile_HappyPath_StreamsBufferAndCleansUp()
    {
        var transferId = StageUpload();
        _files.Setup(f => f.UploadAsync("Files", "docs/a.txt", It.IsAny<Stream>(), "text/plain",
                FileConflictMode.Replace, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry());

        var result = await PlatformFileTools.UploadFile(MockServer.Object, transferId, "Files", "/docs/a.txt",
            "replace", true, "text/plain");

        result.IsSuccess.Should().BeTrue();
        result.Entry!.RtId.Should().Be(FileRtId);
        FileTransferStore.GetUpload(transferId).Should().BeNull();
    }

    [Theory]
    [InlineData("", "Files", "a.txt")]
    [InlineData("id", "", "a.txt")]
    [InlineData("id", "Files", "")]
    public async Task UploadFile_MissingArgs_ReturnsErrorWithoutSdkCall(string transfer, string root, string path)
    {
        var result = await PlatformFileTools.UploadFile(MockServer.Object, transfer, root, path);

        result.IsSuccess.Should().BeFalse();
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadFile_UnknownConflictMode_ReturnsError()
    {
        var result = await PlatformFileTools.UploadFile(MockServer.Object, StageUpload(), "Files", "a.txt", "merge");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("conflict");
    }

    [Fact]
    public async Task UploadFile_UnknownTransfer_ReturnsError()
    {
        var result = await PlatformFileTools.UploadFile(MockServer.Object, "nope", "Files", "a.txt");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadFile_Unauthenticated_ReturnsNotAuthenticated()
    {
        var transferId = StageUpload();
        GivenUnauthenticated();

        var result = await PlatformFileTools.UploadFile(MockServer.Object, transferId, "Files", "a.txt");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task UploadFile_NameConflict_KeepsBufferAndReturnsCode()
    {
        var transferId = StageUpload();
        _files.Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string?>(), FileConflictMode.Fail, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FilesApiException(FileErrorCodes.NameConflict, System.Net.HttpStatusCode.Conflict, "taken"));

        var result = await PlatformFileTools.UploadFile(MockServer.Object, transferId, "Files", "a.txt");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NAME_CONFLICT");
        FileTransferStore.GetUpload(transferId).Should().NotBeNull();
    }

    // ── download_file ───────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadFile_ByPath_StagesBytesInTransferStore()
    {
        _files.Setup(f => f.DownloadAsync("Files", "docs/a.txt", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileDownload(new MemoryStream("hello"u8.ToArray()), "a.txt", "text/plain", 5));

        var result = await PlatformFileTools.DownloadFile(MockServer.Object, "Files", "docs/a.txt");

        result.IsSuccess.Should().BeTrue();
        result.FileName.Should().Be("a.txt");
        result.SizeBytes.Should().Be(5);
        result.DownloadUrlPath.Should().Be($"/file-transfer/download/{result.TransferId}");
        var stored = FileTransferStore.GetDownload(result.TransferId!);
        stored.Should().NotBeNull();
        File.ReadAllText(stored!.FilePath).Should().Be("hello");
    }

    [Fact]
    public async Task DownloadFile_ByRtId_UsesDownloadById()
    {
        _files.Setup(f => f.DownloadByIdAsync(FileRtId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileDownload(new MemoryStream([1, 2, 3]), "../evil/b.bin", null, 3));

        var result = await PlatformFileTools.DownloadFile(MockServer.Object, rtId: FileRtId);

        result.IsSuccess.Should().BeTrue();
        result.FileName.Should().Be("b.bin"); // path parts of a server-sent name are stripped
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Files", null, null)]
    [InlineData("Files", "a.txt", FileRtId)]
    public async Task DownloadFile_AmbiguousOrMissingAddress_ReturnsError(string? root, string? path, string? rtId)
    {
        var result = await PlatformFileTools.DownloadFile(MockServer.Object, root, path, rtId);

        result.IsSuccess.Should().BeFalse();
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadFile_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticated();

        var result = await PlatformFileTools.DownloadFile(MockServer.Object, rtId: FileRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task DownloadFile_NotFound_ReturnsCode()
    {
        _files.Setup(f => f.DownloadAsync("Files", "x", false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FilesApiException(FileErrorCodes.PathNotFound, System.Net.HttpStatusCode.NotFound, null));

        var result = await PlatformFileTools.DownloadFile(MockServer.Object, "Files", "x");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("PATH_NOT_FOUND");
    }

    // ── create_folder ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_HappyPath_CallsSdk()
    {
        _files.Setup(f => f.CreateFolderAsync("Files", "docs", "2026", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry("2026", FileEntryKinds.Folder));

        var result = await PlatformFileTools.CreateFolder(MockServer.Object, "Files", "2026", "/docs/");

        result.IsSuccess.Should().BeTrue();
        result.Entry!.Name.Should().Be("2026");
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("Files", " ")]
    public async Task CreateFolder_MissingArgs_ReturnsError(string root, string name)
    {
        var result = await PlatformFileTools.CreateFolder(MockServer.Object, root, name);

        result.IsSuccess.Should().BeFalse();
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateFolder_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticated();

        var result = await PlatformFileTools.CreateFolder(MockServer.Object, "Files", "x");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task CreateFolder_NameConflict_ReturnsCode()
    {
        _files.Setup(f => f.CreateFolderAsync("Files", null, "x", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FilesApiException(FileErrorCodes.NameConflict, System.Net.HttpStatusCode.Conflict, null, true));

        var result = await PlatformFileTools.CreateFolder(MockServer.Object, "Files", "x");

        result.ErrorCode.Should().Be("NAME_CONFLICT");
    }

    // ── delete_file ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFile_WithoutConfirm_RefusesAndReportsImpact()
    {
        _files.Setup(f => f.GetDeleteImpactAsync(FileRtId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteImpactDto
            {
                Entry = Entry("docs", FileEntryKinds.Folder),
                Stats = new FolderStatsDto { Folders = 2, Files = 10, Bytes = 1024, LinkedFiles = 1 }
            });

        var result = await PlatformFileTools.DeleteFile(MockServer.Object, FileRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true").And.Contain("10 files");
        _files.Verify(f => f.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_ByRtIdConfirmed_Deletes()
    {
        var result = await PlatformFileTools.DeleteFile(MockServer.Object, FileRtId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        _files.Verify(f => f.DeleteAsync(FileRtId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_ByPathConfirmed_ResolvesThenDeletes()
    {
        _files.Setup(f => f.GetEntryAsync("Files", "docs/a.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entry());

        var result = await PlatformFileTools.DeleteFile(MockServer.Object, root: "Files", path: "docs/a.txt",
            confirm: true);

        result.IsSuccess.Should().BeTrue();
        _files.Verify(f => f.DeleteAsync(FileRtId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Files", null, null)]
    [InlineData("Files", "a", FileRtId)]
    public async Task DeleteFile_AmbiguousOrMissingAddress_ReturnsError(string? root, string? path, string? rtId)
    {
        var result = await PlatformFileTools.DeleteFile(MockServer.Object, rtId, root, path, confirm: true);

        result.IsSuccess.Should().BeFalse();
        _files.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteFile_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticated();

        var result = await PlatformFileTools.DeleteFile(MockServer.Object, FileRtId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task DeleteFile_ProtectedRoot_ReturnsCode()
    {
        _files.Setup(f => f.DeleteAsync(FileRtId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FilesApiException(FileErrorCodes.ProtectedRoot, System.Net.HttpStatusCode.Conflict, null, true));

        var result = await PlatformFileTools.DeleteFile(MockServer.Object, FileRtId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("PROTECTED_ROOT");
    }

    // ── risk metadata ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(PlatformFileTools.ListFiles), null)]
    [InlineData(nameof(PlatformFileTools.DownloadFile), null)]
    [InlineData(nameof(PlatformFileTools.CreateFolder), null)]
    [InlineData(nameof(PlatformFileTools.UploadFile), Meshmakers.Octo.Backend.McpServices.Models.McpRiskLevel.Medium)]
    [InlineData(nameof(PlatformFileTools.DeleteFile), Meshmakers.Octo.Backend.McpServices.Models.McpRiskLevel.High)]
    public void Tools_AreRiskClassified(string method, Meshmakers.Octo.Backend.McpServices.Models.McpRiskLevel? expected)
    {
        var attr = typeof(PlatformFileTools).GetMethod(method)!
            .GetCustomAttributes(typeof(Meshmakers.Octo.Backend.McpServices.Models.McpRiskAttribute), false)
            .Cast<Meshmakers.Octo.Backend.McpServices.Models.McpRiskAttribute>().SingleOrDefault();
        attr?.Level.Should().Be(expected);
        if (expected == null) attr.Should().BeNull();
    }
}
