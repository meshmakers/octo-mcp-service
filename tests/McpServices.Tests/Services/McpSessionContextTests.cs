using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for <see cref="McpSessionContext" />.TryGetAccessTokenAsync — the central resolver every
///     SDK-backed tool uses to find the OAuth bearer for downstream calls.
///     <para>
///     Covers the three sources in priority order: (1) fresh session-store token, (2) expired
///     session-store token with refresh-token grant via <see cref="ISessionTokenRefresher" />,
///     (3) HTTP <c>Authorization: Bearer</c> header fallback used by the headless OctoMesh AI
///     worker pod.
///     </para>
///     <para>
///     Plus the AB#5036 binding: the store key is derived from the validated request principal (no shared
///     constant slot, no client-controlled key), and a stored token is verified to belong to that
///     principal before it is handed to a backend service.
///     </para>
/// </summary>
public class McpSessionContextTests
{
    private const string HomeTenant = "tenant-a";
    private const string CallerSubject = "user-1";

    private readonly Mock<IMcpSessionTokenStore> _mockTokenStore = new();
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor = new();
    private readonly Mock<ISessionTokenRefresher> _mockRefresher = new();
    private readonly Mock<McpServer> _mockServer = new();
    private readonly DefaultHttpContext _httpContext = new();
    private readonly ServiceProvider _services;

    public McpSessionContextTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockTokenStore.Object);
        services.AddSingleton(_mockHttpContextAccessor.Object);
        services.AddSingleton(_mockRefresher.Object);
        _services = services.BuildServiceProvider();
        _mockServer.Setup(s => s.Services).Returns(_services);
        _mockHttpContextAccessor.Setup(h => h.HttpContext).Returns(() => _httpContext);

        // Default caller: an authenticated user principal, the shape the JWT bearer handler produces.
        GivenUserPrincipal(HomeTenant, CallerSubject);
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenSessionStoreHasFreshToken_ReturnsThatToken()
    {
        var stored = BoundToken();
        GivenStoredToken(stored);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be(stored,
            "the session store is the primary source — device-flow login path");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "fresh tokens must NOT trigger a refresh");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreEmpty_FallsBackToHttpBearerHeader()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "the HTTP-layer Bearer is the fallback the AI worker pod relies on");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "no stored refresh token → no refresh attempt");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreExpiredAndNoRefreshToken_FallsBackToHttpBearer()
    {
        // Expired stored token but no refresh token (e.g., legacy session) — must NOT try to refresh,
        // fall through to the HTTP-bearer fallback.
        _mockTokenStore.Setup(s => s.GetTokens(TestSessionKey()))
            .Returns(new McpSessionTokens
            {
                AccessToken = BoundToken(),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
                RefreshToken = null
            });
        _httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz");
        _mockRefresher.Verify(r => r.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "no refresh token in store → no refresh attempt");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenExpiredWithRefreshToken_RefreshesAndStoresNewTokens()
    {
        var sessionKey = TestSessionKey();
        _mockTokenStore.Setup(s => s.GetTokens(sessionKey))
            .Returns(new McpSessionTokens
            {
                AccessToken = BoundToken(),
                RefreshToken = "valid-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });

        var freshAccessToken = TestJwt.CreateFull(HomeTenant, CallerSubject, clientId: null, "Reader", "Refreshed");
        var refreshed = new McpSessionTokens
        {
            AccessToken = freshAccessToken,
            RefreshToken = "rotated-refresh",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        _mockRefresher.Setup(r => r.RefreshAsync("valid-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be(freshAccessToken,
            "an expired session token with a refresh token must trigger a refresh");
        _mockTokenStore.Verify(s => s.SetTokens(sessionKey, refreshed), Times.Once,
            "the refreshed tokens must be written back to the store");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenRefreshFails_RemovesTokensAndFallsBackToBearer()
    {
        var sessionKey = TestSessionKey();
        _mockTokenStore.Setup(s => s.GetTokens(sessionKey))
            .Returns(new McpSessionTokens
            {
                AccessToken = BoundToken(),
                RefreshToken = "revoked-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });

        _mockRefresher.Setup(r => r.RefreshAsync("revoked-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        _httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("adapter-minted-bearer-xyz",
            "refresh failure must drop the session token and fall through to the HTTP bearer");
        _mockTokenStore.Verify(s => s.RemoveTokens(sessionKey), Times.Once,
            "stale tokens must be removed so subsequent calls don't keep retrying with the same bad refresh");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenRefreshFailsAndNoBearer_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(TestSessionKey()))
            .Returns(new McpSessionTokens
            {
                AccessToken = BoundToken(),
                RefreshToken = "revoked-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });
        _mockRefresher.Setup(r => r.RefreshAsync("revoked-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpSessionTokens?)null);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("refresh failed, no header bearer → caller is no longer authenticated");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenStoreEmptyAndNoBearerHeader_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("no session-store token, no HTTP bearer → unauthenticated");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_WhenHeaderHasNonBearerScheme_ReturnsNull()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _httpContext.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().BeNull("only Bearer scheme is forwarded to downstream Octo API clients");
    }

    [Fact]
    public async Task TryGetAccessTokenAsync_BearerHeaderIsCaseInsensitive()
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _httpContext.Request.Headers.Authorization = "bearer mixed-case-scheme";

        var token = await McpSessionContext.TryGetAccessTokenAsync(_mockServer.Object);

        token.Should().Be("mixed-case-scheme",
            "RFC 6750 §2.1 — the scheme is case-insensitive; misreading lowercase 'bearer' would mute a valid request");
    }

    // ── AB#5036: the store is bound to the authenticated caller ─────────────────────────────────

    [Fact]
    public async Task ResolveAccessTokenAsync_StoreTokenOfAnotherPrincipal_IsRefused()
    {
        // The core of AB#5036: whatever ended up in the slot, it is only usable by the identity it was
        // issued for. Falling back to the request bearer here would hide the confusion instead of
        // reporting it.
        GivenStoredToken(TestJwt.CreateFull(HomeTenant, "somebody-else", clientId: null, "Admin"));
        _httpContext.Request.Headers.Authorization = "Bearer the-callers-own-bearer";

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be(Constants.SessionTokenNotBoundError);
        result.Error.Should().Contain("does not belong to the authenticated caller");
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_StoreTokenForAnotherTenant_IsRefused()
    {
        // Same subject, different tenant: a device-flow login against another tenant produces exactly
        // this shape, and using it would make the tool act in a tenant the request was not gated for.
        GivenStoredToken(TestJwt.CreateFull("tenant-b", CallerSubject, clientId: null, "Admin"));

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be(Constants.SessionTokenNotBoundError);
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_OpaqueStoreTokenForUserPrincipal_IsRefused()
    {
        // An opaque token carries no claims, so it cannot be bound to the caller at all.
        GivenStoredToken("an-opaque-non-jwt-bearer");

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be(Constants.SessionTokenNotBoundError);
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_RefreshedTokenOfAnotherPrincipal_IsRefused()
    {
        // The binding must be re-checked after a refresh — otherwise an expired foreign token could be
        // laundered into a fresh one.
        _mockTokenStore.Setup(s => s.GetTokens(TestSessionKey()))
            .Returns(new McpSessionTokens
            {
                AccessToken = BoundToken(),
                RefreshToken = "valid-refresh",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });
        _mockRefresher.Setup(r => r.RefreshAsync("valid-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSessionTokens
            {
                AccessToken = TestJwt.CreateFull(HomeTenant, "somebody-else", clientId: null),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.AccessToken.Should().BeNull();
        result.Error.Should().Be(Constants.SessionTokenNotBoundError);
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_ServicePrincipalWithOpaqueStoreToken_IsAllowed()
    {
        // Regression guard for the AI Adapter worker and the mesh-adapter: a client-credentials token has
        // no `sub`, so a subject match is not a question that can be asked of it. Requiring one would lock
        // both out of every tenant (AB#5030 / AB#5032 exemption).
        GivenServicePrincipal("octo-ai-worker");
        GivenStoredToken("an-opaque-service-token");

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.Error.Should().BeNull();
        result.AccessToken.Should().Be("an-opaque-service-token");
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_ServicePrincipalWithoutStoreEntry_UsesItsBearerHeader()
    {
        // The path the AI worker and the mesh-adapter actually take: no store entry at all, token in the
        // Authorization header. It must stay untouched by the binding rules.
        GivenServicePrincipal("octo-ai-worker");
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>())).Returns((McpSessionTokens?)null);
        _httpContext.Request.Headers.Authorization = "Bearer adapter-minted-bearer-xyz";

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.Error.Should().BeNull();
        result.AccessToken.Should().Be("adapter-minted-bearer-xyz");
    }

    [Fact]
    public async Task ResolveAccessTokenAsync_UnauthenticatedRequest_DoesNotReadTheStore()
    {
        // Without a principal there is no slot to read — and no constant fallback slot to read it from.
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _httpContext.Request.Headers.Authorization = "Bearer some-bearer";

        var result = await McpSessionContext.ResolveAccessTokenAsync(_mockServer.Object);

        result.AccessToken.Should().Be("some-bearer");
        _mockTokenStore.Verify(s => s.GetTokens(It.IsAny<string>()), Times.Never);
    }

    // ── AB#5036: session-key derivation ─────────────────────────────────────────────────────────

    [Fact]
    public void TryGetSessionKey_WithoutHeader_IsPerCallerNotShared()
    {
        // The Streamable HTTP transport defaults to HttpServerSessionMode.Stateless and never mints
        // Mcp-Session-Id, so this is the ONLY case that occurs in production. Before AB#5036 it collapsed
        // into one process-wide "default-session" slot.
        GivenUserPrincipal(HomeTenant, "caller-one");
        var first = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        GivenUserPrincipal(HomeTenant, "caller-two");
        var second = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second.Should().NotBe(first, "two callers without the header must not share a store slot");
    }

    [Fact]
    public void TryGetSessionKey_SameHeaderDifferentCallers_StillYieldsDifferentKeys()
    {
        // A caller may name any session id they like; it only partitions within their own namespace.
        _httpContext.Request.Headers["Mcp-Session-Id"] = "the-victims-session";

        GivenUserPrincipal(HomeTenant, "attacker");
        var attackerKey = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        GivenUserPrincipal(HomeTenant, "victim");
        var victimKey = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        attackerKey.Should().NotBe(victimKey,
            "the header must not be able to address another caller's store entry");
    }

    [Fact]
    public void TryGetSessionKey_SameCallerDifferentTenants_YieldsDifferentKeys()
    {
        GivenUserPrincipal(HomeTenant, CallerSubject);
        var inA = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        GivenUserPrincipal("tenant-b", CallerSubject);
        var inB = McpSessionContext.TryGetSessionKey(_mockServer.Object);

        inB.Should().NotBe(inA, "the same subject in a different tenant is a different identity");
    }

    [Fact]
    public void TryGetSessionKey_WithoutAuthenticatedPrincipal_IsNull()
    {
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        McpSessionContext.TryGetSessionKey(_mockServer.Object).Should().BeNull(
            "there is no constant fallback slot any more — no caller means no store token");
    }

    [Fact]
    public void GetCallerLabel_WithoutAuthenticatedPrincipal_IsConstant()
    {
        // The file-transfer ownership tag is informational (transfers authorise on the opaque transfer
        // id), so it may fall back where the token-store key must not.
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        McpSessionContext.GetCallerLabel(_mockServer.Object).Should().Be("anonymous");
    }

    private void GivenUserPrincipal(string tenantId, string subjectId)
    {
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subjectId),
            new Claim("tenant_id", tenantId)
        ], "Bearer"));
    }

    private void GivenServicePrincipal(string clientId)
    {
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", clientId),
            new Claim("tenant_id", HomeTenant)
        ], "Bearer"));
    }

    private void GivenStoredToken(string accessToken)
    {
        _mockTokenStore.Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = accessToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
    }

    /// <summary>
    ///     A JWT bound to the default caller principal — the only shape the store will hand out since
    ///     AB#5036.
    /// </summary>
    private static string BoundToken()
    {
        return TestJwt.CreateFull(HomeTenant, CallerSubject, clientId: null, "Reader");
    }

    /// <summary>
    ///     Derive the same store key the implementation uses, so the mock setups on
    ///     specific keys line up regardless of test-run order or McpServer fallbacks.
    /// </summary>
    private string TestSessionKey()
    {
        return McpSessionContext.TryGetSessionKey(_mockServer.Object)!;
    }
}
