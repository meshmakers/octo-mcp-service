using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Routing;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpServices.Tests.Routing;

/// <summary>
///     AB#6989: a download is single-use. These tests run against a real Kestrel instance on a loopback
///     port, because "the response completed / the client aborted" only exists on a real connection.
/// </summary>
public sealed class FileTransferControllerTests : IAsyncLifetime
{
    private readonly FileTransferStore _store = new();
    private readonly List<string> _files = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IFileTransferStore>(_store);
        // Only the controller under test: the assembly's other controllers need API versioning registered.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FileTransferController).Assembly)
            .ConfigureApplicationPartManager(m =>
            {
                m.FeatureProviders.Remove(m.FeatureProviders.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>().First());
                m.FeatureProviders.Add(new OnlyFileTransferController());
            });
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync(Ct);

        var address = _app.Urls.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private (string Id, string Path) RegisterFile(byte[] content, string session = "sess-1", TimeSpan? ttl = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ab6989_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _files.Add(path);
        return (_store.RegisterDownload(session, path, "dump.bin", ttl), path);
    }

    /// <summary>The delete runs in OnCompleted, after the client already has the last byte.</summary>
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Download_CompleteGet_DeliversFileThenSecondGetIs404AndFileIsGone()
    {
        var content = new byte[256 * 1024];
        Random.Shared.NextBytes(content);
        var (id, path) = RegisterFile(content);

        var first = await _http.GetAsync($"/file-transfer/download/{id}", Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content);

        await EventuallyAsync(() => !File.Exists(path));
        File.Exists(path).Should().BeFalse("the temp file is deleted after the first complete GET");
        var second = await _http.GetAsync($"/file-transfer/download/{id}", Ct);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_AbortedMidTransfer_KeepsEntryForResumeUntilTtl()
    {
        // Big enough that Kestrel cannot have buffered all of it while the client reads 1 KiB.
        var (id, path) = RegisterFile(new byte[96 * 1024 * 1024]);

        using (var response = await _http.GetAsync($"/file-transfer/download/{id}",
                   HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[1024];
            (await body.ReadAsync(buffer, Ct)).Should().BeGreaterThan(0);
        } // disposing without reading the rest aborts the connection

        await Task.Delay(500, Ct);
        File.Exists(path).Should().BeTrue("a half-served download must not be gone");
        _store.GetDownload(id).Should().NotBeNull();

        // The sweeper removes it once the TTL ran out.
        var (shortId, shortPath) = RegisterFile([1, 2, 3], ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        _store.SweepExpired();
        _store.GetDownload(shortId).Should().BeNull();
        File.Exists(shortPath).Should().BeFalse();
    }

    [Fact]
    public async Task Download_RangeThatStopsBeforeTheEnd_KeepsEntry_ResumeToEndCompletesIt()
    {
        var content = new byte[1000];
        Random.Shared.NextBytes(content);
        var (id, path) = RegisterFile(content);

        var head = new HttpRequestMessage(HttpMethod.Get, $"/file-transfer/download/{id}");
        head.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 399);
        var part1 = await _http.SendAsync(head, Ct);
        part1.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part1.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content[..400]);

        await Task.Delay(300, Ct);
        File.Exists(path).Should().BeTrue("the first range did not reach the end of the file");

        var tail = new HttpRequestMessage(HttpMethod.Get, $"/file-transfer/download/{id}");
        tail.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(400, null);
        var part2 = await _http.SendAsync(tail, Ct);
        part2.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part2.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content[400..]);

        await EventuallyAsync(() => !File.Exists(path));
        File.Exists(path).Should().BeFalse("the resumed range delivered the last byte");
        (await _http.GetAsync($"/file-transfer/download/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_ExpiredId_Is404()
    {
        var (id, _) = RegisterFile([1], ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);

        (await _http.GetAsync($"/file-transfer/download/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_UnknownId_Is404()
    {
        (await _http.GetAsync("/file-transfer/download/does-not-exist", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_PutThenOwnerConsumes_UnchangedFlow()
    {
        var (id, _) = _store.ReserveUpload("sess-1", "model.yaml");

        var put = new HttpRequestMessage(HttpMethod.Put, $"/file-transfer/upload/{id}")
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-yaml");
        var response = await _http.SendAsync(put, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _store.GetUpload(id, "sess-2").Should().BeNull();
        _store.GetUpload(id, "sess-1")!.SizeBytes.Should().Be(4);
    }
}

/// <summary>Hides every controller except <see cref="FileTransferController" /> from MVC.</summary>
internal sealed class OnlyFileTransferController : Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider
{
    protected override bool IsController(System.Reflection.TypeInfo typeInfo)
    {
        return typeInfo.AsType() == typeof(FileTransferController);
    }
}
