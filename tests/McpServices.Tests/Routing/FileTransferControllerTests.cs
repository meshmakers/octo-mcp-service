using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Routing;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using Xunit;

namespace McpServices.Tests.Routing;

/// <summary>
///     The file-transfer endpoints. Invariant: an upload or download succeeds only for a request with a valid bearer
///     token whose principal created the transfer, and the transfer id never appears in a URL, log record or span
///     attribute. One test per abuse case; the single-use download and the tool-side session binding stay covered.
///     <para>
///     These tests run against a real Kestrel instance on a loopback port with the real JWT bearer handler (tokens
///     signed with a test key), the real transport policy and the OpenTelemetry ASP.NET Core instrumentation the
///     service uses, because "the response completed / the client aborted" only exists on a real connection and
///     span attributes only exist with the instrumentation.
///     </para>
/// </summary>
public sealed class FileTransferControllerTests : IAsyncLifetime
{
    private const string ApiScope = "octo_api";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("file-transfer-test-signing-key-0123456789-abcdefgh"));

    // Principals. The owner is alice in tenant t1; the others each differ in exactly one dimension.
    private static readonly Caller Alice = new("t1", "alice", "octo-cli");
    private static readonly Caller Bob = new("t1", "bob", "octo-cli");
    private static readonly Caller AliceInOtherTenant = new("t2", "alice", "octo-cli");
    private static readonly Caller AiWorker = new("t1", null, "ai-worker");
    private static readonly Caller MeshAdapter = new("t1", null, "mesh-adapter");
    // A client-credentials token without tenant_id has no owner key and is refused (fail closed).
    private static readonly Caller TenantlessClient = new(null, null, "ai-worker");
    private static readonly Caller ClientInTenant1 = new("t1", null, "mirrored-client");
    private static readonly Caller SameClientInTenant2 = new("t2", null, "mirrored-client");

    private readonly FileTransferStore _store = new();
    private readonly List<string> _files = [];
    private readonly List<Activity> _spans = [];
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _logs = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(new CapturingLoggerProvider(_logs));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IFileTransferStore>(_store);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    IssuerSigningKey = SigningKey
                };
            });
        builder.Services.AddAuthorization(o => McpAuthorizationPolicy.AddMcpTransportPolicy(o, [ApiScope]));
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddInMemoryExporter(_spans));
        // Only the controller under test: the assembly's other controllers need API versioning registered.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FileTransferController).Assembly)
            .ConfigureApplicationPartManager(m =>
            {
                m.FeatureProviders.Remove(m.FeatureProviders.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>().First());
                m.FeatureProviders.Add(new OnlyFileTransferController());
            });
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
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

    // ---------------------------------------------------------------- helpers

    private sealed record Caller(string? TenantId, string? SubjectId, string? ClientId)
    {
        public FileTransferOwner Owner(string session = "mcp-session-1") =>
            new($"{StoreKey}|{session}", Principal, TenantId); // as McpSessionContext.GetFileTransferOwner builds it

        public string? Principal => new McpCallerPrincipal(SubjectId, ClientId, TenantId, []).TransferOwnerKey;

        private string? StoreKey => new McpCallerPrincipal(SubjectId, ClientId, TenantId, []).StoreKeyPrefix;
    }

    private static string Token(Caller caller, bool withScope = true, DateTime? expires = null, SecurityKey? key = null)
    {
        var claims = new List<Claim>();
        if (caller.TenantId != null) claims.Add(new Claim("tenant_id", caller.TenantId));
        if (caller.SubjectId != null) claims.Add(new Claim("sub", caller.SubjectId));
        if (caller.ClientId != null) claims.Add(new Claim("client_id", caller.ClientId));
        if (withScope) claims.Add(new Claim("scope", ApiScope));
        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: exp.AddMinutes(-20),
            expires: exp,
            signingCredentials: new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private (string Id, string Path) RegisterFile(byte[] content, Caller? owner = null, TimeSpan? ttl = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ft_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _files.Add(path);
        return (_store.RegisterDownload((owner ?? Alice).Owner(), path, "dump.bin", ttl), path);
    }

    private static HttpRequestMessage Get(string id, string? token, string path = FileTransferRoutes.DownloadPath,
        string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(FileTransferRoutes.TransferIdHeader, id);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key != null) request.Headers.Add(FileTransferRoutes.TransferKeyHeader, key);
        return request;
    }

    private static HttpRequestMessage Put(string? id, string? token, HttpContent? content = null,
        string path = FileTransferRoutes.UploadPath, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = content ?? new ByteArrayContent([1, 2, 3, 4])
        };
        request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/x-yaml");
        if (id != null) request.Headers.Add(FileTransferRoutes.TransferIdHeader, id);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key != null) request.Headers.Add(FileTransferRoutes.TransferKeyHeader, key);
        return request;
    }

    private string IssueKey(string id, FileTransferDirection direction, Caller? owner = null, TimeSpan? ttl = null) =>
        _store.IssueTransferKey(id, direction, (owner ?? Alice).Owner(), ttl)!.Key;

    private string ReserveFor(Caller caller, out string targetPath, TimeSpan? ttl = null)
    {
        var (id, path) = _store.ReserveUpload(caller.Owner(), "model.yaml", ttl);
        targetPath = path;
        _files.Add(path);
        return id;
    }

    /// <summary>The delete runs in OnCompleted, after the client already has the last byte.</summary>
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private async Task OwnerCanStillUpload(string id, Caller owner)
    {
        var response = await _http.SendAsync(Put(id, Token(owner)), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the owner's slot must stay untouched by a refused request");
        _store.GetUpload(id, owner.Owner())!.SizeBytes.Should().Be(4);
    }

    // ---------------------------------------------------------------- (a) no / invalid token

    [Fact]
    public async Task Upload_WithoutToken_Is401_AndTheSlotStaysForTheOwner()
    {
        var id = ReserveFor(Alice, out var target);

        var response = await _http.SendAsync(Put(id, token: null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(target).Should().BeFalse("nothing is written for an unauthenticated request");
        await OwnerCanStillUpload(id, Alice);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("foreign-key")]
    [InlineData("garbage")]
    public async Task Upload_WithInvalidToken_Is401(string kind)
    {
        var id = ReserveFor(Alice, out var target);
        var token = kind switch
        {
            "expired" => Token(Alice, expires: DateTime.UtcNow.AddMinutes(-1)),
            "foreign-key" => Token(Alice, key: new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x', 48)))),
            _ => "not-a-jwt"
        };

        var response = await _http.SendAsync(Put(id, token), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(target).Should().BeFalse();
        await OwnerCanStillUpload(id, Alice);
    }

    [Fact]
    public async Task Download_WithoutToken_Is401_AndTheEntryStays()
    {
        var (id, path) = RegisterFile([1, 2, 3]);

        var response = await _http.SendAsync(Get(id, token: null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(path).Should().BeTrue();
        _store.GetDownload(id, Alice.Principal).Should().NotBeNull();
    }

    [Fact]
    public async Task Download_WithExpiredToken_Is401()
    {
        var (id, _) = RegisterFile([1, 2, 3]);

        var response = await _http.SendAsync(Get(id, Token(Alice, expires: DateTime.UtcNow.AddMinutes(-1))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _store.GetDownload(id, Alice.Principal).Should().NotBeNull();
    }

    // ---------------------------------------------------------------- (a) foreign principal

    public static TheoryData<string> ForeignCallers =>
        ["foreign-user", "foreign-tenant", "foreign-client", "same-client-other-tenant"];

    private static (Caller Owner, Caller Intruder) Pair(string kind) => kind switch
    {
        "foreign-user" => (Alice, Bob),
        "foreign-tenant" => (Alice, AliceInOtherTenant),
        "foreign-client" => (AiWorker, MeshAdapter),
        // A mirrored client (same client id in two tenants): a token of the other tenant is another owner.
        "same-client-other-tenant" => (ClientInTenant1, SameClientInTenant2),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [MemberData(nameof(ForeignCallers))]
    public async Task Upload_WithForeignPrincipal_Is404_NothingWritten_SlotStaysForTheOwner(string kind)
    {
        var (owner, intruder) = Pair(kind);
        var id = ReserveFor(owner, out var target);

        var response = await _http.SendAsync(Put(id, Token(intruder)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no hint that the id exists");
        File.Exists(target).Should().BeFalse();
        await OwnerCanStillUpload(id, owner);
    }

    [Theory]
    [MemberData(nameof(ForeignCallers))]
    public async Task Download_WithForeignPrincipal_Is404_NothingServed_EntryStaysForTheOwner(string kind)
    {
        var (owner, intruder) = Pair(kind);
        var content = new byte[] { 9, 8, 7 };
        var (id, path) = RegisterFile(content, owner);

        var foreign = await _http.SendAsync(Get(id, Token(intruder)), Ct);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsByteArrayAsync(Ct)).Should().NotEqual(content);

        // A range request is no way around it either.
        var range = Get(id, Token(intruder));
        range.Headers.Range = new RangeHeaderValue(0, 1);
        (await _http.SendAsync(range, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        File.Exists(path).Should().BeTrue();
        var mine = await _http.SendAsync(Get(id, Token(owner)), Ct);
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mine.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content);
    }

    [Fact]
    public async Task McpSessionIdHeader_IsNoIdentity_OnTheHttpSide()
    {
        // The HTTP side binds to the principal only: a foreign caller who names the owner's MCP session gets 404,
        // the owner with another (or no) session header still gets the file.
        var id = ReserveFor(Alice, out _);

        var spoofed = Put(id, Token(Bob));
        spoofed.Headers.Add("Mcp-Session-Id", "mcp-session-1");
        (await _http.SendAsync(spoofed, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var owner = Put(id, Token(Alice));
        owner.Headers.Add("Mcp-Session-Id", "another-session");
        (await _http.SendAsync(owner, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- (a) scope, fail closed, happy paths

    [Fact]
    public async Task TokenWithoutApiScope_Is403_ForUploadAndDownload()
    {
        var id = ReserveFor(Alice, out var target);
        var (downloadId, path) = RegisterFile([1]);

        (await _http.SendAsync(Put(id, Token(Alice, withScope: false)), Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Get(downloadId, Token(Alice, withScope: false)), Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        File.Exists(target).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
        await OwnerCanStillUpload(id, Alice);
    }

    [Fact]
    public async Task CallerWithoutStoreKeyPrefix_IsRefused()
    {
        // A token with neither sub nor client_id has no StoreKeyPrefix: it can never act as an owner.
        var nobody = new Caller("t1", null, null);
        nobody.Principal.Should().BeNull();
        var id = ReserveFor(Alice, out _);
        var (downloadId, _) = RegisterFile([1]);

        // Fail closed like a missing token.
        (await _http.SendAsync(Put(id, Token(nobody)), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get(downloadId, Token(nobody)), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EntryWithoutStoredPrincipal_OrWithTheAnonymousLabel_MatchesNobody()
    {
        // Created by an unidentifiable caller (fail closed): no token unlocks it, not even one whose client id is
        // the anonymous label.
        var anonymousOwner = new FileTransferOwner(McpSessionContext.AnonymousCallerLabel, null);
        var (id, _) = _store.ReserveUpload(anonymousOwner, "model.yaml");
        var labelOwner = new FileTransferOwner(McpSessionContext.AnonymousCallerLabel, McpSessionContext.AnonymousCallerLabel);
        var (labelId, _) = _store.ReserveUpload(labelOwner, "model.yaml");
        var anonymousClient = new Caller(null, null, McpSessionContext.AnonymousCallerLabel);

        foreach (var caller in new[] { Alice, AiWorker, anonymousClient })
        {
            // The tenantless anonymous client has no owner key at all and is refused like a missing token.
            var expected = caller == anonymousClient ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound;
            (await _http.SendAsync(Put(id, Token(caller)), Ct)).StatusCode.Should().Be(expected);
            (await _http.SendAsync(Put(labelId, Token(caller)), Ct)).StatusCode.Should().Be(expected);
        }
    }

    [Fact]
    public async Task Upload_ByTheOwner_UserToken_Is200_AndOnlyTheOwnersSessionConsumesIt()
    {
        var id = ReserveFor(Alice, out _);

        var response = await _http.SendAsync(Put(id, Token(Alice)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("\"sizeBytes\":4").And.Contain(id, "the owner gets its id back as before");
        // The tool side still binds to the MCP session label.
        _store.GetUpload(id, Alice.Owner("mcp-session-2")).Should().BeNull();
        _store.GetUpload(id, Alice.Owner())!.SizeBytes.Should().Be(4);
    }

    [Fact]
    public void TransferOwnerKey_ForAClient_CarriesTheTenant_UserKeyStaysTheStoreKey()
    {
        ClientInTenant1.Principal.Should().Be("c:mirrored-client:t1");
        SameClientInTenant2.Principal.Should().Be("c:mirrored-client:t2");
        AiWorker.Principal.Should().Be("c:ai-worker:t1");
        TenantlessClient.Principal.Should().BeNull("a client token without tenant_id can own no transfer");
        Alice.Principal.Should().Be(new McpCallerPrincipal("alice", "octo-cli", "t1", []).StoreKeyPrefix);
    }

    [Fact]
    public async Task UploadAndDownload_ByTheOwner_ClientCredentialsToken_Is200()
    {
        var id = ReserveFor(AiWorker, out _);
        (await _http.SendAsync(Put(id, Token(AiWorker)), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (downloadId, _) = RegisterFile([5, 6], AiWorker);
        var response = await _http.SendAsync(Get(downloadId, Token(AiWorker)), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(5, 6);
    }

    // ---------------------------------------------------------------- client token without tenant_id

    [Fact]
    public async Task ClientTokenWithoutTenant_IsRefused_ForAnEntryInTheOldFormat_AndWithAKey()
    {
        // An entry stored under the earlier owner key of a tenantless client is not reachable with its token.
        var legacyOwner = new FileTransferOwner("c:ai-worker|s", "c:ai-worker:-");
        var (legacyId, legacyTarget) = _store.ReserveUpload(legacyOwner, "model.yaml");
        _files.Add(legacyTarget);
        (await _http.SendAsync(Put(legacyId, Token(TenantlessClient)), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(legacyTarget).Should().BeFalse();
        var (legacyDownload, legacyPath) = (_store.RegisterDownload(legacyOwner, legacyTarget + ".dl", "f.bin"), legacyTarget + ".dl");
        File.WriteAllBytes(legacyPath, [1]);
        _files.Add(legacyPath);
        (await _http.SendAsync(Get(legacyDownload, Token(TenantlessClient)), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(legacyPath).Should().BeTrue("nothing is served");

        // The tenantless token cannot ride on someone's valid key either (an authenticated caller must be the owner).
        var id = ReserveFor(AiWorker, out var target);
        var key = IssueKey(id, FileTransferDirection.Upload, AiWorker);
        (await _http.SendAsync(Put(id, Token(TenantlessClient), key: key), Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(target).Should().BeFalse();
        _store.HasTransferKey(id).Should().BeTrue("a refused request leaves the owner's key unused");
    }

    // ---------------------------------------------------------------- one-time transfer key

    [Fact]
    public async Task Upload_WithKeyAndNoToken_Is200_AndTheKeyIsSpent()
    {
        var id = ReserveFor(Alice, out _);
        var key = IssueKey(id, FileTransferDirection.Upload);

        var response = await _http.SendAsync(Put(id, token: null, key: key), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _store.HasTransferKey(id).Should().BeFalse();
        // The tool side still binds to the creator's MCP session.
        _store.GetUpload(id, Alice.Owner())!.SizeBytes.Should().Be(4);
        _store.GetUpload(id, Bob.Owner()).Should().BeNull();
    }

    [Fact]
    public async Task Download_WithKeyAndNoToken_Is200_AndSingleUseStays()
    {
        var (id, path) = RegisterFile([4, 5, 6]);
        var key = IssueKey(id, FileTransferDirection.Download);

        var response = await _http.SendAsync(Get(id, token: null, key: key), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(4, 5, 6);
        await EventuallyAsync(() => !File.Exists(path));
        File.Exists(path).Should().BeFalse("a complete GET still ends the download");
        (await _http.SendAsync(Get(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Key_Reused_Is401_EvenWhileTheDownloadStillExists()
    {
        var (id, path) = RegisterFile([1, 2, 3, 4]);
        var key = IssueKey(id, FileTransferDirection.Download);

        // A partial read spends the key but keeps the entry for a resume by the owner's token.
        var partial = Get(id, token: null, key: key);
        partial.Headers.Range = new RangeHeaderValue(0, 0);
        (await _http.SendAsync(partial, Ct)).StatusCode.Should().Be(HttpStatusCode.PartialContent);

        (await _http.SendAsync(Get(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(path).Should().BeTrue();
        (await _http.SendAsync(Get(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Key_ReusedAfterACompletedUpload_Is401()
    {
        var id = ReserveFor(Alice, out _);
        var key = IssueKey(id, FileTransferDirection.Upload);
        (await _http.SendAsync(Put(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _http.SendAsync(Put(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Key_Expired_Is401_AndTheTransferStaysForTheOwnersToken()
    {
        var id = ReserveFor(Alice, out var target);
        var key = IssueKey(id, FileTransferDirection.Upload, ttl: TimeSpan.Zero);
        var (downloadId, path) = RegisterFile([7]);
        var downloadKey = IssueKey(downloadId, FileTransferDirection.Download, ttl: TimeSpan.Zero);

        (await _http.SendAsync(Put(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get(downloadId, token: null, key: downloadKey), Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        File.Exists(target).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
        _store.HasTransferKey(id).Should().BeFalse("an expired key is dropped");
        await OwnerCanStillUpload(id, Alice);
    }

    [Fact]
    public async Task Key_OfAnotherTransfer_Is401()
    {
        var mine = ReserveFor(Alice, out var target);
        var other = ReserveFor(Bob, out _);
        IssueKey(mine, FileTransferDirection.Upload);
        var bobsKey = IssueKey(other, FileTransferDirection.Upload, Bob);

        (await _http.SendAsync(Put(mine, token: null, key: bobsKey), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // A key issued in another tenant (the same subject's transfer there) does not open this one either.
        var otherTenant = ReserveFor(AliceInOtherTenant, out _);
        var otherTenantKey = IssueKey(otherTenant, FileTransferDirection.Upload, AliceInOtherTenant);
        (await _http.SendAsync(Put(mine, token: null, key: otherTenantKey), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(target).Should().BeFalse();
        _store.HasTransferKey(mine).Should().BeTrue();
        _store.HasTransferKey(other).Should().BeTrue("a key presented for another transfer is not spent");
    }

    [Theory]
    [MemberData(nameof(ForeignCallers))]
    public async Task Key_WithTheTokenOfAnotherPrincipal_Is401_AndStaysUsableForTheOwner(string kind)
    {
        // Covers another user, another tenant, another client and the same client id with a token of another tenant.
        var (owner, intruder) = Pair(kind);
        var id = ReserveFor(owner, out var target);
        var key = IssueKey(id, FileTransferDirection.Upload, owner);
        var (downloadId, _) = RegisterFile([3, 3], owner);
        var downloadKey = IssueKey(downloadId, FileTransferDirection.Download, owner);

        (await _http.SendAsync(Put(id, Token(intruder), key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get(downloadId, Token(intruder), key: downloadKey), Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(target).Should().BeFalse();

        (await _http.SendAsync(Put(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _http.SendAsync(Get(downloadId, Token(owner), key: downloadKey), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Key_ForTheOtherDirection_Is401()
    {
        var uploadId = ReserveFor(Alice, out var target);
        var uploadKey = IssueKey(uploadId, FileTransferDirection.Upload);
        var (downloadId, path) = RegisterFile([1]);
        var downloadKey = IssueKey(downloadId, FileTransferDirection.Download);

        // The upload key on the download endpoint (its own id), and the download key on the upload endpoint.
        (await _http.SendAsync(Get(uploadId, token: null, key: uploadKey), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Put(downloadId, token: null, key: downloadKey), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        File.Exists(target).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
        _store.HasTransferKey(uploadId).Should().BeTrue();
        _store.HasTransferKey(downloadId).Should().BeTrue();
    }

    [Fact]
    public async Task Key_ConcurrentConsume_ExactlyOneRequestWins()
    {
        var (id, _) = RegisterFile(new byte[64 * 1024]);
        var key = IssueKey(id, FileTransferDirection.Download);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => _http.SendAsync(Get(id, token: null, key: key), HttpCompletionOption.ResponseHeadersRead, Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(11);
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task Key_ConcurrentUploads_OnlyOneWritesTheSlot()
    {
        var id = ReserveFor(Alice, out _);
        var key = IssueKey(id, FileTransferDirection.Upload);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _http.SendAsync(Put(id, token: null, key: key), Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => r.StatusCode)
            .Should().OnlyContain(c => c == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EveryKeyRefusal_AndAMissingCredential_GetTheSameBody()
    {
        var id = ReserveFor(Alice, out _);
        var valid = IssueKey(id, FileTransferDirection.Upload);
        var expiredId = ReserveFor(Alice, out _);
        var expired = IssueKey(expiredId, FileTransferDirection.Upload, ttl: TimeSpan.Zero);
        var foreignId = ReserveFor(Bob, out _);
        IssueKey(foreignId, FileTransferDirection.Upload, Bob);
        var (downloadId, _) = RegisterFile([1]);
        var usedKey = IssueKey(downloadId, FileTransferDirection.Download);
        var partial = Get(downloadId, token: null, key: usedKey);
        partial.Headers.Range = new RangeHeaderValue(0, 0);
        (await _http.SendAsync(partial, Ct)).StatusCode.Should().Be(HttpStatusCode.PartialContent);

        var refusals = new List<HttpRequestMessage>
        {
            Put(id, token: null),                                          // missing key and token
            Put(id, token: null, key: "garbage"),                          // wrong key
            Put(id, token: null, key: ""),                                 // empty key
            Put(expiredId, token: null, key: expired),                     // expired
            Put(foreignId, token: null, key: valid),                       // key of another transfer (foreign)
            Put(id, Token(Bob), key: valid),                               // token of another principal
            Get(downloadId, token: null, key: usedKey),                    // reused
            Get(id, token: null, key: valid),                              // wrong direction
            Put("does-not-exist", token: null, key: valid),                // unknown id
        };
        var twoKeys = Put(id, token: null);
        twoKeys.Headers.Add(FileTransferRoutes.TransferKeyHeader, [valid, valid]);
        refusals.Add(twoKeys);

        var bodies = new List<string>();
        var headers = new List<string>();
        foreach (var request in refusals)
        {
            var response = await _http.SendAsync(request, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            bodies.Add(await response.Content.ReadAsStringAsync(Ct));
            // Every header except the date: same set, same values.
            headers.Add(string.Join("\n", response.Headers.Concat(response.Content.Headers)
                .Where(h => h.Key != "Date")
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => h.Key + ": " + string.Join(",", h.Value))));
        }

        bodies.Distinct().Should().ContainSingle("no refusal may tell expired, used, foreign or missing apart");
        headers.Distinct().Should().ContainSingle("the headers must not tell them apart either");
        bodies[0].Should().NotContain(id).And.NotContain(valid);
        (await _http.SendAsync(Put(id, token: null, key: valid), Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "none of the refusals spent the owner's key");
    }

    [Fact]
    public async Task CreatorsToken_WinsOverAWrongKey_AndLeavesTheKeyUnspent()
    {
        var (id, _) = RegisterFile([2, 4]);
        var key = IssueKey(id, FileTransferDirection.Download);

        // A partial read keeps the entry, so the key's state is observable afterwards.
        var request = Get(id, Token(Alice), key: "garbage");
        request.Headers.Range = new RangeHeaderValue(0, 0);
        var response = await _http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        _store.HasTransferKey(id).Should().BeTrue("the bearer path does not touch the key");
        (await _http.SendAsync(Get(id, token: null, key: key), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SpansLogsAndBodies_NeverCarryTheTransferKey()
    {
        var id = ReserveFor(Alice, out _);
        var key = IssueKey(id, FileTransferDirection.Upload);
        var (downloadId, _) = RegisterFile([1, 2]);
        var downloadKey = IssueKey(downloadId, FileTransferDirection.Download);

        var bodies = new List<string>();
        foreach (var request in new[]
                 {
                     Put(id, Token(Bob), key: key), Put(id, token: null, key: key), Put(id, token: null, key: key),
                     Get(downloadId, token: null, key: downloadKey), Get(downloadId, token: null, key: downloadKey)
                 })
        {
            var response = await _http.SendAsync(request, Ct);
            bodies.Add(await response.Content.ReadAsStringAsync(Ct));
        }

        await EventuallyAsync(() => _spans.Count >= 5);
        foreach (var secret in new[] { key, downloadKey })
        {
            bodies.Should().NotContain(b => b.Contains(secret));
            _spans.SelectMany(a => a.TagObjects).Select(t => t.Value?.ToString() ?? "")
                .Should().NotContain(v => v.Contains(secret));
            _spans.Select(a => a.DisplayName).Should().NotContain(v => v.Contains(secret));
            _logs.Select(l => l.Text).Should().NotContain(t => t.Contains(secret));
        }
    }

    // ---------------------------------------------------------------- (b) id never in path or query

    [Fact]
    public async Task OldPathRoutes_Are404_EvenForTheOwner_AndLeaveTheEntriesAlone()
    {
        var id = ReserveFor(Alice, out var target);
        var (downloadId, path) = RegisterFile([1]);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/file-transfer/upload/{id}") { Content = new ByteArrayContent([1]) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-yaml");
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        (await _http.SendAsync(put, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var get = new HttpRequestMessage(HttpMethod.Get, $"/file-transfer/download/{downloadId}");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        (await _http.SendAsync(get, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        File.Exists(target).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
        await OwnerCanStillUpload(id, Alice);
    }

    [Fact]
    public async Task IdInTheQueryString_IsNotAccepted()
    {
        var id = ReserveFor(Alice, out var target);
        var (downloadId, path) = RegisterFile([1]);

        var put = await _http.SendAsync(Put(null, Token(Alice), path: $"{FileTransferRoutes.UploadPath}?transferId={id}"), Ct);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the id is read from the header only");

        var get = new HttpRequestMessage(HttpMethod.Get, $"{FileTransferRoutes.DownloadPath}?transferId={downloadId}");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        (await _http.SendAsync(get, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        File.Exists(target).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task SpansAndLogs_NeverCarryTheTransferId_AndErrorBodiesDoNotEchoIt()
    {
        var id = ReserveFor(Alice, out _);
        var (downloadId, _) = RegisterFile([1, 2, 3]);
        var (expiredId, _) = RegisterFile([1], ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(30, Ct);
        var secrets = new[] { id, downloadId, expiredId };

        var responses = new List<HttpResponseMessage>
        {
            await _http.SendAsync(Put(id, token: null), Ct), // 401
            await _http.SendAsync(Put(id, Token(Bob)), Ct), // 404
            await _http.SendAsync(Put(id, Token(Alice, withScope: false)), Ct), // 403
            await _http.SendAsync(Put(id, Token(Alice)), Ct), // 200
            await _http.SendAsync(Put(id, Token(Alice)), Ct), // 404, consumed
            await _http.SendAsync(Get(downloadId, Token(Bob)), Ct), // 404
            await _http.SendAsync(Get(downloadId, Token(Alice)), Ct), // 200
            await _http.SendAsync(Get(downloadId, Token(Alice)), Ct), // 404, single use
            await _http.SendAsync(Get(expiredId, Token(Alice)), Ct) // 404, expired
        };
        responses.Select(r => (int)r.StatusCode).Should().Equal(401, 404, 403, 200, 404, 404, 200, 404, 404);

        foreach (var response in responses.Where(r => !r.IsSuccessStatusCode))
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            secrets.Should().NotContain(s => body.Contains(s), "an error response must not echo the id");
            response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value)
                .Should().NotContain(v => secrets.Any(v.Contains));
        }

        await EventuallyAsync(() => _spans.Count >= responses.Count);
        _spans.Should().HaveCountGreaterThanOrEqualTo(responses.Count);
        foreach (var span in _spans)
        {
            span.GetTagItem("url.path").Should().BeOneOf(FileTransferRoutes.UploadPath, FileTransferRoutes.DownloadPath);
            span.DisplayName.Should().NotContainAny(secrets);
            foreach (var tag in span.TagObjects)
            {
                (tag.Value?.ToString() ?? string.Empty).Should().NotContainAny(secrets,
                    $"span attribute {tag.Key} must not carry the transfer id");
            }

            span.TagObjects.Select(t => t.Key).Should().NotContain(k =>
                k.Contains("x_transfer_id", StringComparison.OrdinalIgnoreCase)
                || k.Contains("x-transfer-id", StringComparison.OrdinalIgnoreCase));
        }

        _logs.Where(l => l.Level >= LogLevel.Information).Select(l => l.Text)
            .Should().NotContain(t => secrets.Any(t.Contains), "no log record at Information or above names the id");
    }

    // ---------------------------------------------------------------- (c) atomic slot, aborted PUT, single use, expiry

    [Fact]
    public async Task ConcurrentSecondPut_Is409_AndTheFirstPutCompletes()
    {
        var id = ReserveFor(Alice, out _);
        var gate = new GatedContent([1, 2, 3], [4, 5]);

        var first = _http.SendAsync(Put(id, Token(Alice), gate), Ct);
        await gate.FirstChunkSent.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await EventuallyAsync(() => _store.IsUploadClaimed(id));

        var second = await _http.SendAsync(Put(id, Token(Alice), new ByteArrayContent([7, 7, 7, 7, 7, 7, 7])), Ct);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(Ct)).Should().NotContain(id);

        gate.Release();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        var upload = _store.GetUpload(id, Alice.Owner())!;
        upload.SizeBytes.Should().Be(5);
        (await File.ReadAllBytesAsync(upload.FilePath, Ct)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task AbortedPut_InvalidatesTheSlot_AndDeletesThePartialFile()
    {
        var id = ReserveFor(Alice, out var target);
        var gate = new GatedContent([1, 2, 3], [4]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = _http.SendAsync(Put(id, Token(Alice), gate), cts.Token);
        await gate.FirstChunkSent.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await EventuallyAsync(() => _store.IsUploadClaimed(id));
        await cts.CancelAsync(); // the client goes away mid-body
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();

        await EventuallyAsync(() => !_store.IsUploadPending(id));
        _store.IsUploadPending(id).Should().BeFalse("an aborted PUT ends the reservation");
        await EventuallyAsync(() => !File.Exists(target));
        File.Exists(target).Should().BeFalse();
        (await _http.SendAsync(Put(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a new prepare_file_upload is needed after a failed PUT");
        _store.GetUpload(id, Alice.Owner()).Should().BeNull();
    }

    [Fact]
    public async Task Upload_ExpiredReservation_Is404()
    {
        var id = ReserveFor(Alice, out _, ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(30, Ct);

        (await _http.SendAsync(Put(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_CompleteGet_DeliversFileThenSecondGetIs404AndFileIsGone()
    {
        var content = new byte[256 * 1024];
        Random.Shared.NextBytes(content);
        var (id, path) = RegisterFile(content);

        var first = await _http.SendAsync(Get(id, Token(Alice)), Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content);

        await EventuallyAsync(() => !File.Exists(path));
        File.Exists(path).Should().BeFalse("the temp file is deleted after the first complete GET");
        (await _http.SendAsync(Get(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_AbortedMidTransfer_KeepsEntryForResumeUntilTtl()
    {
        // Big enough that Kestrel cannot have buffered all of it while the client reads 1 KiB.
        var (id, path) = RegisterFile(new byte[96 * 1024 * 1024]);

        using (var response = await _http.SendAsync(Get(id, Token(Alice)), HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[1024];
            (await body.ReadAsync(buffer, Ct)).Should().BeGreaterThan(0);
        } // disposing without reading the rest aborts the connection

        await Task.Delay(500, Ct);
        File.Exists(path).Should().BeTrue("a half-served download must not be gone");
        _store.GetDownload(id, Alice.Principal).Should().NotBeNull();

        // The sweeper removes it once the TTL ran out.
        var (shortId, shortPath) = RegisterFile([1, 2, 3], ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        _store.SweepExpired();
        _store.GetDownload(shortId, Alice.Principal).Should().BeNull();
        File.Exists(shortPath).Should().BeFalse();
    }

    [Fact]
    public async Task Download_RangeThatStopsBeforeTheEnd_KeepsEntry_ResumeToEndCompletesIt()
    {
        var content = new byte[1000];
        Random.Shared.NextBytes(content);
        var (id, path) = RegisterFile(content);

        var head = Get(id, Token(Alice));
        head.Headers.Range = new RangeHeaderValue(0, 399);
        var part1 = await _http.SendAsync(head, Ct);
        part1.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part1.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content[..400]);

        await Task.Delay(300, Ct);
        File.Exists(path).Should().BeTrue("the first range did not reach the end of the file");

        var tail = Get(id, Token(Alice));
        tail.Headers.Range = new RangeHeaderValue(400, null);
        var part2 = await _http.SendAsync(tail, Ct);
        part2.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part2.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(content[400..]);

        await EventuallyAsync(() => !File.Exists(path));
        File.Exists(path).Should().BeFalse("the resumed range delivered the last byte");
        (await _http.SendAsync(Get(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_ExpiredId_Is404()
    {
        var (id, _) = RegisterFile([1], ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);

        (await _http.SendAsync(Get(id, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownId_AndMissingHeader_AreRefused()
    {
        (await _http.SendAsync(Get("does-not-exist", Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.SendAsync(Put("does-not-exist", Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.SendAsync(Put(null, Token(Alice)), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>Request body that sends a first chunk, then waits until the test releases (or cancels) it.</summary>
internal sealed class GatedContent(byte[] first, byte[] rest) : HttpContent
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstChunkSent => _firstSent.Task;

    public void Release() => _release.TrySetResult();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(first, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        _firstSent.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        await stream.WriteAsync(rest, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false; // chunked, so the server cannot know the end before the client sends it
    }
}

/// <summary>Collects every formatted log record (with its structured values) of the test host.</summary>
internal sealed class CapturingLoggerProvider(ConcurrentQueue<(LogLevel Level, string Text)> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(sink);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Text)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> kv
                ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            sink.Enqueue((logLevel, $"{formatter(state, exception)} {values} {exception}"));
        }
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
