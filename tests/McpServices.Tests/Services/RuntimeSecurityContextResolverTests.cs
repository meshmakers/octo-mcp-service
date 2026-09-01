using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for <see cref="RuntimeSecurityContextResolver" /> — the AB#5030 replacement for the
///     parameterless system sessions the direct-engine (family 2/3) tools used to open.
///     <para>
///     The caller identity is taken from the validated request principal, never from the session token
///     store (whose key is the client-supplied <c>Mcp-Session-Id</c> header). The store is consulted only
///     on the cross-tenant exchange path, and there the token must bind back to the request principal.
///     </para>
/// </summary>
public class RuntimeSecurityContextResolverTests : TestBase
{
    // ── Same-tenant path: identity comes from the principal, the store is never touched ─────────

    [Fact]
    public async Task ResolveAsync_UserPrincipalForSameTenant_ReturnsCallerContext()
    {
        GivenAuthenticatedCaller(DefaultTestTenantId, "user-42", "Reader", "Writer");

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.Error.Should().BeNull();
        result.ResolvedTenantId.Should().Be(DefaultTestTenantId);
        result.SecurityContext.Should().NotBeNull();
        result.SecurityContext!.IsSystem.Should().BeFalse("the resolver must never produce a system context");
        result.SecurityContext.SubjectId.Should().Be("user-42");
        result.SecurityContext.Roles.Should().BeEquivalentTo("Reader", "Writer");
    }

    [Fact]
    public async Task ResolveAsync_SameTenant_DoesNotConsultTheSessionTokenStore()
    {
        // The store key derives from the client-set Mcp-Session-Id header and falls back to a shared
        // slot, so the normal path must not read an identity out of it at all.
        GivenRuntimeCallerToken(TestJwt.CreateFull(
            "someone-elses-tenant", "someone-else", clientId: null, "Admin"));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.Error.Should().BeNull();
        result.SecurityContext!.SubjectId.Should().Be(DefaultCallerSubjectId,
            "the identity must come from the validated principal, not from the session store");
        result.SecurityContext.Roles.Should().NotContain("Admin");
        MockRuntimeSessionTokenStore.Verify(s => s.GetTokens(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_SubjectMappedToNameIdentifier_IsRecognisedAsUser()
    {
        // ConfigureJwtBearerOptions leaves MapInboundClaims = true, so `sub` reaches the principal as
        // ClaimTypes.NameIdentifier. Reading only "sub" would misclassify this as a service token and
        // hand it the tenant-gate exemption.
        GivenRequestPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "mapped-user"),
            new Claim("tenant_id", DefaultTestTenantId)
        ], "Bearer")));
        MockTenantResolution.Setup(t => t.ResolveTenantId(ForeignTenantId)).Returns(ForeignTenantId);
        GivenTokenExchange(_ => null);

        var sameTenant = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);
        var foreignTenant = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        sameTenant.SecurityContext!.SubjectId.Should().Be("mapped-user");
        foreignTenant.Error.Should().Contain("denied",
            "a mapped subject still makes this a USER principal, which the tenant gate applies to");
    }

    [Fact]
    public async Task ResolveAsync_RolesAreReadFromBothClaimTypes()
    {
        GivenRequestPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-42"),
            new Claim("tenant_id", DefaultTestTenantId),
            new Claim(ClaimTypes.Role, "MappedRole"),
            new Claim("role", "ShortRole"),
            new Claim(ClaimTypes.Role, "MappedRole")
        ], "Bearer")));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.SecurityContext!.Roles.Should().BeEquivalentTo("MappedRole", "ShortRole");
    }

    // ── Tenant gate ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_UserPrincipalWithoutTenantClaim_IsDenied()
    {
        // TenantAuthorizationMiddleware answers 403 for this case; the per-tool tenant parameter must
        // not be more permissive than the route gate (K2).
        GivenCallerWithoutTenantClaim();

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Contain("denied").And.Contain("tenant_id");
    }

    [Fact]
    public async Task ResolveAsync_UserPrincipalForDifferentTenant_IsDeniedWhenExchangeDoesNotApply()
    {
        GivenForeignTenantCall();

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.ResolvedTenantId.Should().Be(ForeignTenantId);
        result.Error.Should().Contain("denied")
            .And.Contain(ForeignTenantId)
            .And.Contain(DefaultTestTenantId);
    }

    [Fact]
    public async Task ResolveAsync_ServicePrincipalForDifferentTenant_IsAllowedWithClientIdSubject()
    {
        // No subject at all ⇒ client-credentials service token (AI Adapter worker / mesh-adapter).
        // The gate exempts these on purpose, mirroring TenantAuthorizationMiddleware (AB#5032).
        MockTenantResolution.Setup(t => t.ResolveTenantId(ForeignTenantId)).Returns(ForeignTenantId);
        GivenServicePrincipalCaller("octo-ai-worker", DefaultTestTenantId, "ServiceRole");

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.Error.Should().BeNull("client-credentials principals are exempt from the tenant match");
        result.SecurityContext.Should().NotBeNull();
        result.SecurityContext!.SubjectId.Should().Be("octo-ai-worker",
            "with no subject the client id identifies the caller");
        result.SecurityContext.Roles.Should().BeEquivalentTo("ServiceRole");
        result.SecurityContext.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_WithoutAuthenticatedPrincipal_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task ResolveAsync_WithoutHttpContextAccessor_IsDeniedNotCrashed()
    {
        // A host that never wired IHttpContextAccessor must degrade into a denial, never into an
        // exception escaping the tool.
        var provider = new TestServiceProvider();
        var server = new Mock<McpServer>();
        server.Setup(s => s.Services).Returns(provider);

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            server.Object, MockTenantResolution.Object, DefaultTestTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task ResolveAsync_WhenTenantCannotBeResolved_ReturnsResolverMessage()
    {
        MockTenantResolution
            .Setup(t => t.ResolveTenantId(It.IsAny<string?>()))
            .Throws(new InvalidOperationException("No tenant ID specified."));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, null);

        result.SecurityContext.Should().BeNull();
        result.ResolvedTenantId.Should().BeNull();
        result.Error.Should().Be("No tenant ID specified.");
    }

    [Fact]
    public async Task ResolveAsync_WhenTenantResolutionThrowsAnythingElse_StillDoesNotThrow()
    {
        // The "never throws" contract must not depend on which exception type a foreign
        // ITenantResolutionService implementation picks (N2).
        MockTenantResolution
            .Setup(t => t.ResolveTenantId(It.IsAny<string?>()))
            .Throws(new FormatException("Tenant id is malformed."));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, "!!!");

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Be("Tenant id is malformed.");
    }

    // ── Cross-tenant path (AB#4338) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_CrossTenant_UsesTheExchangedShadowIdentity()
    {
        // The identity in tenant B is the B-shadow user: different subject id, roles resolved in B.
        // Reusing the home principal here would leak A's roles into B.
        GivenSuccessfulTenantExchange(ForeignTenantId, "shadow-user-in-b", "BReader");

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.Error.Should().BeNull();
        result.SecurityContext.Should().NotBeNull();
        result.SecurityContext!.SubjectId.Should().Be("shadow-user-in-b");
        result.SecurityContext.Roles.Should().BeEquivalentTo("BReader");
        result.SecurityContext.Roles.Should().NotContain(DefaultCallerRole,
            "the home tenant's roles must not travel into the target tenant");
        result.SecurityContext.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_StoreTokenOfAnotherPrincipal_IsDenied()
    {
        // K1: the store key comes from the client-supplied Mcp-Session-Id header (shared fallback
        // slot). A token found there that does not bind to THIS request principal must not be used
        // as the subject_token of an exchange.
        GivenSuccessfulTenantExchange(ForeignTenantId, "shadow-user-in-b", "BReader");
        GivenRuntimeCallerToken(TestJwt.CreateFull(
            DefaultTestTenantId, "a-completely-different-user", clientId: null, "Admin"));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Contain("denied").And.Contain("does not belong to the authenticated caller");
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_StoreTokenForAnotherTenant_IsDenied()
    {
        GivenSuccessfulTenantExchange(ForeignTenantId, "shadow-user-in-b", "BReader");
        GivenRuntimeCallerToken(TestJwt.CreateFull(
            "yet-another-tenant", DefaultCallerSubjectId, clientId: null, DefaultCallerRole));

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Contain("does not belong to the authenticated caller");
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_OpaqueHomeToken_IsDenied()
    {
        // K3: an unparseable home token cannot be bound to the principal, so it must not be exchanged.
        GivenSuccessfulTenantExchange(ForeignTenantId, "shadow-user-in-b", "BReader");
        GivenRuntimeCallerToken("an-opaque-non-jwt-bearer");

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Contain("does not belong to the authenticated caller");
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_OpaqueExchangedToken_IsDenied()
    {
        // K3: an exchanged token whose tenant_id cannot be read cannot be proven to be scoped to the
        // target tenant — fail closed instead of building a claimless context that acts in tenant B.
        MockTenantResolution.Setup(t => t.ResolveTenantId(ForeignTenantId)).Returns(ForeignTenantId);
        GivenAuthenticatedCaller();
        GivenTokenExchange(_ => "an-opaque-non-jwt-bearer");

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().Contain("denied").And.Contain("unreadable");
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_WithoutSessionToken_ReturnsNotAuthenticated()
    {
        MockTenantResolution.Setup(t => t.ResolveTenantId(ForeignTenantId)).Returns(ForeignTenantId);
        GivenAuthenticatedCaller();
        GivenNoRuntimeCallerToken();

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task ResolveAsync_CrossTenant_FailedExchange_SaysDeniedNotNotAuthenticated()
    {
        // M2: "Not authenticated" would send AI clients into a pointless device-flow re-login. The
        // caller IS authenticated — they just may not act on this tenant.
        GivenFailingTenantExchange();

        var result = await RuntimeSecurityContextResolver.ResolveAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.SecurityContext.Should().BeNull();
        result.Error.Should().NotStartWith("Not authenticated");
        result.Error.Should().Contain("denied")
            .And.Contain("exchange failed")
            .And.Contain(ForeignTenantId);
    }

    [Fact]
    public async Task ResolveTenantAccessAsync_AppliesTheSameGate()
    {
        GivenForeignTenantCall();

        var result = await RuntimeSecurityContextResolver.ResolveTenantAccessAsync(
            MockServer.Object, MockTenantResolution.Object, ForeignTenantId);

        result.Error.Should().Contain("denied");
    }
}
