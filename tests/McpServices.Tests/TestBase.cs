using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using ModelContextProtocol.Server;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts;
using System.Runtime.Serialization;
using System.Security.Claims;
using CkDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace McpServices.Tests;

public abstract class TestBase
{
    protected Mock<McpServer> MockServer { get; private set; }
    protected TestServiceProvider TestServiceProvider { get; private set; }
    protected Mock<IOctoHttpContextAccessor> MockHttpContextAccessor { get; private set; }
    protected Mock<ITenantResolutionService> MockTenantResolution { get; private set; }
    protected Mock<ICkCacheService> MockCkCacheService { get; private set; }
    protected Mock<ITenantRepository> MockTenantRepository { get; private set; }
    protected Mock<IOctoSession> MockSession { get; private set; }

    /// <summary>
    ///     The <see cref="ISecureSessionFactory" /> face of <see cref="MockTenantRepository" />. Production
    ///     tenant repositories implement this interface, and
    ///     <see cref="TenantRepositorySecurityExtensions.GetSessionAsync(ITenantRepository, RtSecurityContext)" />
    ///     silently falls back to the parameterless system session when they do not — so a mock without it
    ///     makes every security-context call site look correct while enforcing nothing. Verify against this
    ///     mock to assert which <see cref="RtSecurityContext" /> a tool actually opened its session with.
    /// </summary>
    protected Mock<ISecureSessionFactory> MockSecureSessionFactory { get; private set; }

    /// <summary>
    ///     The HTTP context every tool call runs under. Its <see cref="HttpContext.User" /> is the
    ///     validated request principal <see cref="RuntimeSecurityContextResolver" /> derives the caller
    ///     identity from (AB#5030).
    /// </summary>
    protected DefaultHttpContext TestHttpContext { get; private set; }

    /// <summary>
    ///     Tenant every mock in this base resolves to.
    /// </summary>
    protected const string DefaultTestTenantId = "test-tenant";

    /// <summary>
    ///     Subject id carried by the default caller token.
    /// </summary>
    protected const string DefaultCallerSubjectId = "test-subject";

    /// <summary>
    ///     Role carried by the default caller token.
    /// </summary>
    protected const string DefaultCallerRole = "TestRole";

    /// <summary>
    ///     Session token store used by the direct-engine (family 2/3) tools through
    ///     <see cref="McpSessionContext" /> → <see cref="RuntimeSecurityContextResolver" /> (AB#5030).
    ///     Pre-seeded with a user token for <see cref="DefaultTestTenantId" /> so those tools have a
    ///     caller identity by default.
    ///     <para>
    ///     <see cref="ToolTestBase" /> re-registers its own <c>MockTokenStore</c> over this one (the
    ///     test service provider is a dictionary — last registration wins), so SDK-backed tools keep
    ///     their explicit <c>GivenAuthenticated()</c> / <c>GivenUnauthenticated()</c> semantics.
    ///     </para>
    /// </summary>
    protected Mock<IMcpSessionTokenStore> MockRuntimeSessionTokenStore { get; private set; }

    protected TestBase()
    {
        MockServer = new Mock<McpServer>();
        TestServiceProvider = new TestServiceProvider();
        MockHttpContextAccessor = new Mock<IOctoHttpContextAccessor>();
        MockTenantResolution = new Mock<ITenantResolutionService>();
        MockCkCacheService = new Mock<ICkCacheService>();
        MockTenantRepository = new Mock<ITenantRepository>();
        MockSession = new Mock<IOctoSession>();
        MockRuntimeSessionTokenStore = new Mock<IMcpSessionTokenStore>();
        MockSecureSessionFactory = MockTenantRepository.As<ISecureSessionFactory>();
        TestHttpContext = new DefaultHttpContext();

        // Setup basic mocks
        MockServer.Setup(s => s.Services).Returns(TestServiceProvider);

        // Register services in test service provider
        TestServiceProvider.RegisterService(MockRuntimeSessionTokenStore.Object);

        // The request principal the direct-engine tools resolve their caller identity from (AB#5030).
        var systemHttpContextAccessor = new Mock<IHttpContextAccessor>();
        systemHttpContextAccessor.Setup(a => a.HttpContext).Returns(() => TestHttpContext);
        TestServiceProvider.RegisterService(systemHttpContextAccessor.Object);
        TestServiceProvider.RegisterService(MockHttpContextAccessor.Object);
        TestServiceProvider.RegisterService(MockTenantResolution.Object);
        TestServiceProvider.RegisterService(MockCkCacheService.Object);
        TestServiceProvider.RegisterService<IRtEntityToDtoMapper>(new RtEntityToDtoMapper(MockCkCacheService.Object));

        // Setup HttpContextAccessor
        MockHttpContextAccessor.Setup(h => h.GetTenantRepositoryAsync())
            .ReturnsAsync(MockTenantRepository.Object);

        // Setup TenantResolutionService
        MockTenantResolution.Setup(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()))
            .ReturnsAsync(MockTenantRepository.Object);
        MockTenantResolution.Setup(t => t.ResolveTenantId(It.IsAny<string?>()))
            .Returns("test-tenant");

        // Setup TenantRepository
        MockTenantRepository.Setup(tr => tr.TenantId).Returns("test-tenant");

        // The secure overload is the only sanctioned way for a direct-engine tool to open a session.
        MockSecureSessionFactory
            .Setup(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()))
            .ReturnsAsync(MockSession.Object);

        // …and the parameterless system session is a hard failure: it is exactly what AB#5030 removes,
        // and TenantRepositorySecurityExtensions degrades into it SILENTLY for any repository that does
        // not implement ISecureSessionFactory. Throwing here makes a missed or regressed call site fail
        // its test instead of quietly running with system privileges.
        MockTenantRepository.Setup(tr => tr.GetSessionAsync())
            .ThrowsAsync(new InvalidOperationException(
                "The parameterless system session is forbidden (AB#5030) — open the session with the "
                + "caller's RtSecurityContext via GetSessionAsync(securityContext)."));

        // Default caller for the direct-engine tools: an authenticated user principal homed in the
        // default tenant, plus the matching session token for the cross-tenant exchange path.
        GivenAuthenticatedCaller();

        // Setup CkCacheService mocks for RtEntityToDtoMapper
        SetupCkCacheServiceMocks();
    }

    /// <summary>
    ///     Makes <see cref="MockRuntimeSessionTokenStore" /> hand out the given access token for the
    ///     current MCP session. Since AB#5030 the store is only consulted on the cross-tenant exchange
    ///     path — the same-tenant identity comes from the request principal.
    /// </summary>
    protected void GivenRuntimeCallerToken(string accessToken)
    {
        MockRuntimeSessionTokenStore
            .Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = accessToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
    }

    /// <summary>
    ///     Removes the caller's session token. The request principal is left untouched, so this models a
    ///     caller whose transport token is valid but who has no device-flow session — enough for
    ///     same-tenant calls, not enough for the cross-tenant exchange.
    /// </summary>
    protected void GivenNoRuntimeCallerToken()
    {
        MockRuntimeSessionTokenStore
            .Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns((McpSessionTokens?)null);
    }

    /// <summary>
    ///     Installs <paramref name="principal" /> as the validated request principal on
    ///     <see cref="TestHttpContext" />.
    /// </summary>
    protected void GivenRequestPrincipal(ClaimsPrincipal principal)
    {
        TestHttpContext.User = principal;
    }

    /// <summary>
    ///     Builds a principal in the shape the JWT bearer handler actually produces. Because
    ///     <c>ConfigureJwtBearerOptions</c> leaves <c>MapInboundClaims</c> at its default of <c>true</c>,
    ///     the JWT's <c>sub</c> arrives as <see cref="ClaimTypes.NameIdentifier" /> and <c>role</c> as
    ///     <see cref="ClaimTypes.Role" />, while <c>tenant_id</c> and <c>client_id</c> keep their JWT
    ///     names. Tests must mirror that or they pin a shape production never sees.
    /// </summary>
    /// <param name="tenantId">Value of the <c>tenant_id</c> claim; omitted when null.</param>
    /// <param name="subjectId">Value of the mapped subject claim; omitted when null (service token).</param>
    /// <param name="clientId">Value of the <c>client_id</c> claim; omitted when null.</param>
    /// <param name="roles">Role claim values, emitted under <see cref="ClaimTypes.Role" />.</param>
    protected static ClaimsPrincipal BuildPrincipal(
        string? tenantId, string? subjectId, string? clientId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (subjectId != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subjectId));
        }

        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>
    ///     Default arrangement: an authenticated user principal homed in <see cref="DefaultTestTenantId" />
    ///     with <see cref="DefaultCallerSubjectId" /> / <see cref="DefaultCallerRole" />, plus a matching
    ///     session token so the cross-tenant exchange path has a home token that binds to this principal.
    /// </summary>
    protected void GivenAuthenticatedCaller(
        string tenantId = DefaultTestTenantId,
        string subjectId = DefaultCallerSubjectId,
        params string[] roles)
    {
        var effectiveRoles = roles.Length > 0 ? roles : [DefaultCallerRole];
        GivenRequestPrincipal(BuildPrincipal(tenantId, subjectId, clientId: null, effectiveRoles));
        GivenRuntimeCallerToken(TestJwt.CreateFull(tenantId, subjectId, clientId: null, effectiveRoles));
    }

    /// <summary>
    ///     No authenticated principal on the request and no session token — the direct-engine tools must
    ///     report "Not authenticated".
    /// </summary>
    protected void GivenUnauthenticatedCaller()
    {
        // An identity with no authentication type is NOT authenticated — the shape UseAuthentication
        // leaves behind when no bearer was presented.
        GivenRequestPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));
        GivenNoRuntimeCallerToken();
    }

    /// <summary>
    ///     A client-credentials service principal: <c>client_id</c> but no subject at all. This is the
    ///     class of caller the tenant gate deliberately exempts (AB#5030 / AB#5032).
    /// </summary>
    protected void GivenServicePrincipalCaller(
        string clientId = "octo-ai-worker",
        string? tenantId = DefaultTestTenantId,
        params string[] roles)
    {
        GivenRequestPrincipal(BuildPrincipal(tenantId, subjectId: null, clientId, roles));
    }

    /// <summary>
    ///     A user principal (subject present) whose token carries no <c>tenant_id</c> claim — the case
    ///     <c>TenantAuthorizationMiddleware</c> answers 403 for, and the gate must deny too.
    /// </summary>
    protected void GivenCallerWithoutTenantClaim(string subjectId = DefaultCallerSubjectId)
    {
        GivenRequestPrincipal(BuildPrincipal(tenantId: null, subjectId, clientId: null, DefaultCallerRole));
    }

    /// <summary>
    ///     Tenant a test points a tool at that the caller was NOT issued a token for.
    /// </summary>
    protected const string ForeignTenantId = "other-tenant";

    /// <summary>
    ///     Arranges a cross-tenant call the tenant gate must refuse (AB#5030): the tool is pointed at
    ///     <paramref name="foreignTenantId" /> while the caller's principal stays homed in
    ///     <see cref="DefaultTestTenantId" />. The registered token exchanger deliberately hands back a
    ///     token that is still scoped to the home tenant — the case the gate exists for, since a
    ///     well-behaved exchange would have produced a token for the target tenant.
    /// </summary>
    protected void GivenForeignTenantCall(string foreignTenantId = ForeignTenantId)
    {
        MockTenantResolution.Setup(t => t.ResolveTenantId(foreignTenantId)).Returns(foreignTenantId);

        GivenAuthenticatedCaller();
        var homeToken = TestJwt.CreateFull(
            DefaultTestTenantId, DefaultCallerSubjectId, clientId: null, DefaultCallerRole);

        GivenTokenExchange(_ => homeToken);
    }

    /// <summary>
    ///     Arranges a SUCCESSFUL cross-tenant exchange (AB#4338): the caller stays homed in
    ///     <see cref="DefaultTestTenantId" />, and the exchange returns a token issued for
    ///     <paramref name="targetTenantId" /> carrying the target tenant's shadow identity. The resolver
    ///     must build its security context from that shadow identity, not from the home principal.
    /// </summary>
    /// <param name="targetTenantId">Tenant the call is pointed at.</param>
    /// <param name="shadowSubjectId">Subject id of the shadow user in the target tenant.</param>
    /// <param name="shadowRoles">Roles resolved for the shadow user in the target tenant.</param>
    protected void GivenSuccessfulTenantExchange(
        string targetTenantId, string shadowSubjectId, params string[] shadowRoles)
    {
        MockTenantResolution.Setup(t => t.ResolveTenantId(targetTenantId)).Returns(targetTenantId);
        GivenAuthenticatedCaller();

        var exchangedToken = TestJwt.CreateFull(
            targetTenantId, shadowSubjectId, clientId: null, shadowRoles);
        GivenTokenExchange(_ => exchangedToken);
    }

    /// <summary>
    ///     Arranges a cross-tenant call whose exchange FAILS (the identity server refuses — the caller has
    ///     no access to the target tenant). The resolver must say so, not "Not authenticated".
    /// </summary>
    protected void GivenFailingTenantExchange(string targetTenantId = ForeignTenantId)
    {
        MockTenantResolution.Setup(t => t.ResolveTenantId(targetTenantId)).Returns(targetTenantId);
        GivenAuthenticatedCaller();
        GivenTokenExchange(_ => null);
    }

    /// <summary>
    ///     Registers an <see cref="ITenantTokenExchanger" /> that produces the access token
    ///     <paramref name="tokenForTenant" /> returns for the requested tenant, or no token at all when it
    ///     returns null.
    /// </summary>
    protected void GivenTokenExchange(Func<string, string?> tokenForTenant)
    {
        var exchanger = new Mock<ITenantTokenExchanger>();
        exchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string tenant, CancellationToken _) =>
            {
                var token = tokenForTenant(tenant);
                return token == null
                    ? null
                    : new McpSessionTokens
                    {
                        AccessToken = token,
                        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
                    };
            });
        TestServiceProvider.RegisterService(exchanger.Object);
    }

    private void SetupCkCacheServiceMocks()
    {
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns((string _, RtCkId<CkTypeId> typeId) => new CkTypeGraph(typeId.FullName, new CkDto.CkCompiledTypeDto()
            {
                TypeId = typeId.ElementId,
                Attributes = new List<CkDto.CkTypeAttributeDto>()
            }));
    }
}