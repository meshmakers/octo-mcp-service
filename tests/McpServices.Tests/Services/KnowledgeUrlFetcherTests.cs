using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#5037 — the SSRF policy on server-side knowledge-source fetches. The stored URL is
///     tenant-writable and the request leaves from inside the cluster, so the guard has to hold for
///     scheme, post-DNS address range, redirect targets, response size and wall-clock time.
/// </summary>
public class KnowledgeUrlFetcherTests
{
    private const string PublicUrl = "https://docs.example.com/claude.md";

    private readonly StubDnsResolver _dns = new();
    private readonly StubHandler _handler = new();

    private KnowledgeFetchOptions _options = new();

    private IKnowledgeUrlFetcher BuildFetcher() =>
        new KnowledgeUrlFetcher(
            new StubHttpClientFactory(_handler),
            new KnowledgeUrlPolicy(_dns),
            Options.Create(_options));

    private Task<KnowledgeFetchResult> FetchAsync(string url) =>
        BuildFetcher().FetchAsync(url, CancellationToken.None);

    // ── Scheme allow-list ───────────────────────────────────────────────────

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://internal.svc:70/1")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("data:text/plain;base64,aGk=")]
    public async Task Fetch_DisallowedScheme_Refused(string url)
    {
        var result = await FetchAsync(url);

        result.Body.Should().BeNull();
        result.Error.Should().Contain("not allowed");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_RelativeUrl_Refused()
    {
        // Note: an absolute *path* like "/etc/passwd" parses as an absolute file:// URI on Unix, so it
        // is caught one step later by the scheme check. Either way it never reaches the network.
        var result = await FetchAsync("docs/claude.md");

        result.Error.Should().Contain("not an absolute URL");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_AbsoluteLocalPath_RefusedAsFileScheme()
    {
        var result = await FetchAsync("/etc/passwd");

        result.Error.Should().Contain("not allowed");
        _handler.Requests.Should().BeEmpty();
    }

    // ── Address ranges, checked AFTER name resolution ───────────────────────

    [Theory]
    [InlineData("169.254.169.254")] // cloud instance metadata
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("10.1.2.3")]        // RFC 1918
    [InlineData("172.16.0.9")]      // RFC 1918
    [InlineData("192.168.7.7")]     // RFC 1918
    [InlineData("100.64.0.1")]      // RFC 6598 CGNAT
    [InlineData("0.0.0.0")]         // "this network"
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fd00:ec2::254")]   // IPv6 instance metadata
    [InlineData("fe80::1")]         // IPv6 link-local
    [InlineData("fc00::1234")]      // IPv6 unique-local
    [InlineData("::ffff:10.0.0.5")] // IPv4-mapped private
    public async Task Fetch_NameResolvingIntoBlockedRange_Refused(string address)
    {
        // The whole point of resolving first: the host name says nothing about where it points.
        _dns.Map("totally-normal.example.com", address);

        var result = await FetchAsync("https://totally-normal.example.com/x");

        result.Body.Should().BeNull();
        result.Error.Should().Contain("blocked address");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_IpLiteralHost_RefusedWithoutDns()
    {
        var result = await FetchAsync("http://169.254.169.254/latest/meta-data/");

        result.Error.Should().Contain("blocked address");
        _dns.Lookups.Should().BeEmpty();
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_MixedRecords_RefusedOnTheBlockedOne()
    {
        // Which record the connection would pick is not ours to decide — one blocked address is enough.
        _dns.Map("split.example.com", "93.184.216.34", "10.0.0.5");

        var result = await FetchAsync("https://split.example.com/x");

        result.Error.Should().Contain("10.0.0.5");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_UnresolvableHost_RefusedFailClosed()
    {
        _dns.Fail("nope.example.com");

        var result = await FetchAsync("https://nope.example.com/x");

        result.Error.Should().Contain("could not be resolved");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_PublicHost_ReturnsBody()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _handler.Respond(PublicUrl, "# hello");

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().BeNull();
        result.Body.Should().Be("# hello");
        result.Truncated.Should().BeFalse();
    }

    // ── Operator allow-list ─────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_AllowListedHost_BypassesTheAddressCheck()
    {
        _dns.Map("wiki.internal", "10.0.0.5");
        _options.AllowedHosts.Add("wiki.internal");
        _handler.Respond("https://wiki.internal/page", "internal doc");

        var result = await FetchAsync("https://wiki.internal/page");

        result.Error.Should().BeNull();
        result.Body.Should().Be("internal doc");
    }

    [Fact]
    public async Task Fetch_AllowListSuffixEntry_MatchesSubdomainsOnly()
    {
        _dns.Map("docs.corp.example", "10.0.0.5");
        _dns.Map("corp.example", "10.0.0.6");
        _options.AllowedHosts.Add(".corp.example");
        _handler.Respond("https://docs.corp.example/x", "ok");

        (await FetchAsync("https://docs.corp.example/x")).Body.Should().Be("ok");
        // The bare apex is NOT covered by a leading-dot entry.
        (await FetchAsync("https://corp.example/x")).Error.Should().Contain("blocked address");
    }

    [Fact]
    public async Task Fetch_AllowListDoesNotWaiveTheSchemeCheck()
    {
        _options.AllowedHosts.Add("wiki.internal");

        var result = await FetchAsync("file://wiki.internal/etc/passwd");

        result.Error.Should().Contain("not allowed");
    }

    [Fact]
    public async Task Fetch_AllowPrivateNetworks_DisablesTheAddressCheck()
    {
        _options.AllowPrivateNetworks = true;
        _handler.Respond("http://10.0.0.5/x", "ok");

        (await FetchAsync("http://10.0.0.5/x")).Body.Should().Be("ok");
    }

    // ── Redirects ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_RedirectIntoBlockedRange_RefusedAndNotFollowed()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _handler.Redirect(PublicUrl, "http://169.254.169.254/latest/meta-data/");

        var result = await FetchAsync(PublicUrl);

        result.Body.Should().BeNull();
        result.Error.Should().Contain("redirect to");
        result.Error.Should().Contain("blocked address");
        // Only the first hop was ever requested.
        _handler.Requests.Should().ContainSingle().Which.Should().Be(PublicUrl);
    }

    [Fact]
    public async Task Fetch_RedirectToDisallowedScheme_Refused()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _handler.Redirect(PublicUrl, "file:///etc/passwd");

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().Contain("redirect to");
        result.Error.Should().Contain("not allowed");
    }

    [Fact]
    public async Task Fetch_RedirectToPublicHost_IsFollowed()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _dns.Map("cdn.example.com", "93.184.216.35");
        _handler.Redirect(PublicUrl, "https://cdn.example.com/claude.md");
        _handler.Respond("https://cdn.example.com/claude.md", "moved body");

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().BeNull();
        result.Body.Should().Be("moved body");
        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fetch_TooManyRedirects_Refused()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options.MaxRedirects = 1;
        _handler.Redirect(PublicUrl, "https://docs.example.com/a");
        _handler.Redirect("https://docs.example.com/a", "https://docs.example.com/b");

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().Contain("redirect");
        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fetch_MaxRedirectsZero_DoesNotFollowAtAll()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options.MaxRedirects = 0;
        _handler.Redirect(PublicUrl, "https://docs.example.com/a");

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().Contain("redirect");
        _handler.Requests.Should().ContainSingle();
    }

    // ── Size limit ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_DeclaredContentLengthOverLimit_RefusedBeforeStreaming()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options.MaxResponseBytes = 16;
        _handler.Respond(PublicUrl, new string('x', 1000), setContentLength: true);

        var result = await FetchAsync(PublicUrl);

        result.Body.Should().BeNull();
        result.Error.Should().Contain("over the 16-byte");
    }

    [Fact]
    public async Task Fetch_UndeclaredOversizedBody_TruncatedNotFailed()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options.MaxResponseBytes = 10;
        _handler.Respond(PublicUrl, new string('x', 1000), setContentLength: false);

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result.Body.Should().HaveLength(10);
    }

    [Fact]
    public async Task Fetch_BodyExactlyAtLimit_NotMarkedTruncated()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options.MaxResponseBytes = 10;
        _handler.Respond(PublicUrl, new string('x', 10), setContentLength: false);

        var result = await FetchAsync(PublicUrl);

        result.Truncated.Should().BeFalse();
        result.Body.Should().HaveLength(10);
    }

    // ── Time limit ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_SlowResponse_AbortedByTheTimeout()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _options = new KnowledgeFetchOptions { TimeoutSeconds = 1 };
        _handler.Stall(PublicUrl);

        var result = await FetchAsync(PublicUrl);

        result.Body.Should().BeNull();
        result.Error.Should().Contain("timeout");
    }

    // ── Ordinary HTTP failures still surface, not as refusals ───────────────

    [Fact]
    public async Task Fetch_NonSuccessStatus_ReportsTheStatus()
    {
        _dns.Map("docs.example.com", "93.184.216.34");
        _handler.Status(PublicUrl, HttpStatusCode.NotFound);

        var result = await FetchAsync(PublicUrl);

        result.Error.Should().Contain("404");
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class StubDnsResolver : IHostAddressResolver
    {
        private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Lookups { get; } = [];

        public void Map(string host, params string[] addresses) =>
            _map[host] = addresses.Select(IPAddress.Parse).ToArray();

        public void Fail(string host) => _failing.Add(host);

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups.Add(host);
            if (_failing.Contains(host))
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            return Task.FromResult(_map.TryGetValue(host, out var addresses) ? addresses : []);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stalling = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void Respond(string url, string body, bool setContentLength = true) =>
            _responses[url] = () =>
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var content = setContentLength
                    ? new ByteArrayContent(bytes)
                    : (HttpContent)new StreamContent(new NonSeekableStream(bytes));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            };

        public void Redirect(string url, string location) =>
            _responses[url] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            };

        public void Status(string url, HttpStatusCode status) =>
            _responses[url] = () => new HttpResponseMessage(status);

        public void Stall(string url) => _stalling.Add(url);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            if (_stalling.Contains(url))
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }

            return _responses.TryGetValue(url, out var factory)
                ? factory()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    ///     Content stream without a known length, so <c>Content-Length</c> is absent and the fetcher has
    ///     to enforce the byte budget while reading.
    /// </summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toCopy = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
