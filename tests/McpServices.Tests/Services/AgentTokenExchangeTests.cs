using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#9729 (option A): an agent token (scope <c>octo_agent</c>) passes the MCP transport only as a client token, is
///     exchanged in exactly one place (<see cref="McpSessionContext" />) for an <c>octo_api</c> token through the
///     confidential client <c>octo-mcpServices-agent-exchange</c>, and is never itself returned for a downstream call.
/// </summary>
public class AgentTokenExchangeTests
{
    private const string Tenant = "acme";
    private const string AgentClient = "agent-builder";
    private const string AgentToken = "agent-token-with-octo_agent";
    private const string Secret = "exchange-secret-from-vault";

    // ---------- transport policy ----------

    [Fact]
    public void Transport_AdmitsAClientTokenWithTheAgentScope()
    {
        McpAuthorizationPolicy.IsAgentClientToken(Principal(sub: null, clientId: AgentClient, "octo_agent"))
            .Should().BeTrue();
    }

    [Fact]
    public void Transport_RefusesAUserTokenWithTheAgentScope()
    {
        McpAuthorizationPolicy.IsAgentClientToken(Principal(sub: "user-1", clientId: "octo-cli", "octo_agent"))
            .Should().BeFalse("the agent scope belongs to client tokens only");
        McpAuthorizationPolicy.HasRequiredScope(Principal(sub: "user-1", clientId: "octo-cli", "octo_agent"),
            ["octo_api"]).Should().BeFalse();
    }

    [Fact]
    public void Transport_RefusesAClientTokenWithoutAnyAcceptedScope()
    {
        McpAuthorizationPolicy.IsAgentClientToken(Principal(sub: null, clientId: AgentClient, "openid"))
            .Should().BeFalse();
    }

    // ---------- the one exchange point ----------

    [Fact]
    public async Task AgentCaller_GetsTheExchangedToken_NeverItsOwnToken()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        exchanger.Setup(e => e.ExchangeAsync(AgentToken, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync("octo-api-token-with-act");
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger.Object);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, TestContext.Current.CancellationToken);

        resolved.AccessToken.Should().Be("octo-api-token-with-act");
        resolved.Error.Should().BeNull();
        exchanger.Verify(e => e.ExchangeAsync(AgentToken, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentCaller_WhenTheExchangeFails_IsRefused_AndTheAgentTokenIsNotUsed()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        exchanger.Setup(e => e.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger.Object);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, TestContext.Current.CancellationToken);

        resolved.AccessToken.Should().BeNull("an agent token is never sent to a platform API");
        resolved.Error.Should().Be(Constants.AgentExchangeFailedError);
    }

    [Fact]
    public async Task AgentCaller_WithoutAnExchangerRegistered_IsRefused()
    {
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger: null);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, TestContext.Current.CancellationToken);

        resolved.AccessToken.Should().BeNull();
        resolved.Error.Should().Be(Constants.AgentExchangeFailedError);
    }

    [Fact]
    public async Task AgentCaller_AskingForAnotherTenant_IsRefused_WithoutAnyExchange()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        var tenantExchanger = new Mock<ITenantTokenExchanger>();
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger.Object,
            tenantExchanger.Object);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, "other-tenant", TestContext.Current.CancellationToken);

        resolved.AccessToken.Should().BeNull();
        resolved.Error.Should().Be(Constants.AgentOtherTenantError("other-tenant"));
        exchanger.VerifyNoOtherCalls();
        tenantExchanger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BotHelper_AgentCallerAskingForAnotherTenant_IsRefused_WithoutAnyExchange()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger.Object,
            tenantResolution: "other-tenant");

        var bot = await BotClientContext.TryBuildAsync(server, "other-tenant");

        bot.Client.Should().BeNull();
        bot.Error.Should().Be(Constants.AgentOtherTenantError("other-tenant"));
        exchanger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BotHelper_UserAskingForAnotherTenant_IsLeftToTheBotService()
    {
        var server = Server(Principal(sub: "user-1", clientId: "octo-cli", "octo_api"), null,
            tenantResolution: "other-tenant");

        var bot = await BotClientContext.TryBuildAsync(server, "other-tenant");

        bot.Error.Should().NotBe(Constants.AgentOtherTenantError("other-tenant"),
            "only agents are bound to their own tenant on the MCP side; a user's cross-tenant job is judged by the Bot service");
    }

    [Fact]
    public async Task AgentCaller_InItsOwnTenant_GetsTheExchangedToken()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        exchanger.Setup(e => e.ExchangeAsync(AgentToken, Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestJwt.CreateFull(Tenant, subjectId: null, clientId: AgentClient));
        var server = Server(Principal(sub: null, clientId: AgentClient, "octo_agent"), exchanger.Object);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, Tenant, TestContext.Current.CancellationToken);

        resolved.Error.Should().BeNull();
        resolved.AccessToken.Should().NotBe(AgentToken);
    }

    [Fact]
    public async Task CallerWithoutTheAgentScope_IsNotExchanged()
    {
        var exchanger = new Mock<IAgentTokenExchanger>();
        var server = Server(Principal(sub: null, clientId: "octo-pipeline-sa-1", "octo_api"), exchanger.Object);

        var resolved = await McpSessionContext.ResolveAccessTokenAsync(server, TestContext.Current.CancellationToken);

        resolved.AccessToken.Should().Be(AgentToken, "an ordinary client keeps using its own bearer token");
        exchanger.VerifyNoOtherCalls();
    }

    // ---------- the exchanger ----------

    [Fact]
    public async Task Exchanger_PresentsTheConfidentialClient_InTheAgentsTenant_ForOctoApi()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{ "access_token": "exchanged", "token_type": "Bearer", "expires_in": 300 }""");

        var token = await Exchanger(handler, Secret).ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken);

        token.Should().Be("exchanged");
        var body = handler.Bodies.Single();
        body.Should().Contain("client_id=octo-mcpServices-agent-exchange");
        body.Should().Contain("client_secret=" + Secret);
        body.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange");
        body.Should().Contain("subject_token=" + AgentToken);
        body.Should().Contain("acr_values=tenant%3A" + Tenant);
        body.Should().Contain("scope=octo_api");
        body.Should().NotContain("octo-mcpServices-device", "the public device client never exchanges an agent token");
    }

    [Fact]
    public async Task Exchanger_CachesTheResultPerAgentToken()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{ "access_token": "exchanged", "expires_in": 300 }""");
        var exchanger = Exchanger(handler, Secret);

        await exchanger.ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken);
        await exchanger.ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken);
        await exchanger.ExchangeAsync("another-agent-token", Tenant, TestContext.Current.CancellationToken);

        handler.Bodies.Should().HaveCount(2, "one exchange per agent token and lifetime");
    }

    [Fact]
    public async Task Exchanger_WithoutASecret_ExchangesNothing()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{ "access_token": "exchanged", "expires_in": 300 }""");

        var token = await Exchanger(handler, secret: null).ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken);

        token.Should().BeNull("no secret configured means no exchange and every downstream call refused");
        handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Exchanger_WhenIdentityRefuses_ReturnsNull_AndCachesNothing()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""");
        var exchanger = Exchanger(handler, Secret);

        (await exchanger.ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken)).Should().BeNull();
        (await exchanger.ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken)).Should().BeNull();

        handler.Bodies.Should().HaveCount(2, "a refusal is not cached");
    }

    // ---------- the client registration ----------

    [Fact]
    public void Registration_WithoutASecret_RegistersNoClient()
    {
        AgentExchangeClientRegistration.BuildClient(new McpServiceOptions()).Should().BeNull();
        AgentExchangeClientRegistration.Fingerprint(new McpServiceOptions())
            .Should().Be(AgentExchangeClientRegistration.NotConfiguredFingerprint);
    }

    [Fact]
    public void Registration_IsAConfidentialExchangeOnlyClient_WithOctoApiOnly()
    {
        var client = AgentExchangeClientRegistration.BuildClient(new McpServiceOptions
            { AgentExchangeClientSecret = Secret })!;

        client.ClientId.Should().Be("octo-mcpServices-agent-exchange");
        client.RequireClientSecret.Should().BeTrue();
        client.ClientSecret.Should().Be(Secret);
        client.AllowedGrantTypes.Should().BeEquivalentTo(["urn:ietf:params:oauth:grant-type:token-exchange"]);
        client.AllowedScopes.Should().BeEquivalentTo(["octo_api"]);
        client.AllowOfflineAccess.Should().BeFalse();
        client.RedirectUris.Should().BeEmpty();
    }

    [Fact]
    public void Registration_FingerprintChangesWithTheSecret_AndHidesIt()
    {
        var first = AgentExchangeClientRegistration.Fingerprint(new McpServiceOptions { AgentExchangeClientSecret = "a" });
        var second = AgentExchangeClientRegistration.Fingerprint(new McpServiceOptions { AgentExchangeClientSecret = "b" });

        first.Should().NotBe(second, "a rotated secret must be registered again");
        first.Should().HaveLength(8);
    }

    [Fact]
    public async Task Rotation_ChangedFingerprint_ResetsTheIdentityDataVersion_AndStoresTheNewFingerprint_InOneTransaction()
    {
        var (systemContext, session) = SystemContext(storedFingerprint: "old00000");

        var reset = await AgentExchangeClientRegistration.ResetIdentityDataVersionIfChangedAsync(systemContext.Object,
            "McpIdentityDataVersion", "new11111");

        reset.Should().BeTrue();
        systemContext.Verify(c => c.SetConfigurationAsync(session.Object, "McpIdentityDataVersion",
            It.Is<object>(v => v is DefaultConfigurationVersion && ((DefaultConfigurationVersion)v).Version == -1)), Times.Once);
        systemContext.Verify(c => c.SetConfigurationAsync(session.Object, AgentExchangeClientRegistration.FingerprintKey,
            "new11111"), Times.Once);
        session.Verify(s => s.StartTransaction(), Times.Once);
        session.Verify(s => s.CommitTransactionAsync(), Times.Once);
        session.Verify(s => s.AbortTransactionAsync(), Times.Never);
    }

    [Fact]
    public async Task Rotation_SameFingerprint_ChangesNothing()
    {
        var (systemContext, session) = SystemContext(storedFingerprint: "same2222");

        var reset = await AgentExchangeClientRegistration.ResetIdentityDataVersionIfChangedAsync(systemContext.Object,
            "McpIdentityDataVersion", "same2222");

        reset.Should().BeFalse();
        systemContext.Verify(c => c.SetConfigurationAsync(It.IsAny<IOctoAdminSession>(), It.IsAny<string>(),
            It.IsAny<object>()), Times.Never);
        systemContext.Verify(c => c.SetConfigurationAsync(It.IsAny<IOctoAdminSession>(), It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
        session.Verify(s => s.CommitTransactionAsync(), Times.Never);
    }

    [Fact]
    public async Task Rotation_WriteFails_AbortsTheTransaction_AndRethrows()
    {
        var (systemContext, session) = SystemContext(storedFingerprint: null);
        systemContext.Setup(c => c.SetConfigurationAsync(It.IsAny<IOctoAdminSession>(), AgentExchangeClientRegistration.FingerprintKey,
            It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("store down"));

        var act = () => AgentExchangeClientRegistration.ResetIdentityDataVersionIfChangedAsync(systemContext.Object,
            "McpIdentityDataVersion", "new11111");

        await act.Should().ThrowAsync<InvalidOperationException>();
        session.Verify(s => s.AbortTransactionAsync(), Times.Once);
        session.Verify(s => s.CommitTransactionAsync(), Times.Never);
    }

    [Fact]
    public async Task Exchanger_UsesItsOwnCertificateValidatingClient_NotTheGeneralIdentityClient()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            "{\"access_token\":\"x\",\"expires_in\":300,\"token_type\":\"Bearer\"}");
        var factory = new SingleHandlerFactory(handler);

        await Exchanger(handler, Secret, factory).ExchangeAsync(AgentToken, Tenant, TestContext.Current.CancellationToken);

        factory.Names.Should().Equal(AgentTokenExchanger.AgentExchangeHttpClientName);
    }

    [Fact]
    public void ExchangeClient_ValidatesTheCertificate_UnlessTheDevelopmentOptionIsSet()
    {
        static HttpClientHandler PrimaryHandler(bool acceptAny)
        {
            var services = new ServiceCollection();
            services.AddOptions<McpServiceOptions>().Configure(o => o.AgentExchangeAcceptAnyServerCertificate = acceptAny);
            AgentTokenExchanger.AddExchangeHttpClient(services);
            var builder = services.BuildServiceProvider().GetRequiredService<IHttpMessageHandlerFactory>();
            var handler = builder.CreateHandler(AgentTokenExchanger.AgentExchangeHttpClientName);
            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler!;
            }

            return (HttpClientHandler)handler;
        }

        PrimaryHandler(acceptAny: false).ServerCertificateCustomValidationCallback.Should().BeNull();
        PrimaryHandler(acceptAny: true).ServerCertificateCustomValidationCallback.Should().NotBeNull();
        new McpServiceOptions().AgentExchangeAcceptAnyServerCertificate.Should().BeFalse("validation is the default");
    }

    // ---------- helpers ----------

    private static (Mock<ISystemContext> Context, Mock<IOctoAdminSession> Session) SystemContext(string? storedFingerprint)
    {
        var session = new Mock<IOctoAdminSession>();
        session.Setup(s => s.CommitTransactionAsync()).Returns(Task.CompletedTask);
        session.Setup(s => s.AbortTransactionAsync()).Returns(Task.CompletedTask);
        var context = new Mock<ISystemContext>();
        context.Setup(c => c.GetAdminSessionAsync()).ReturnsAsync(session.Object);
        context.Setup(c => c.GetConfigurationAsync(session.Object, AgentExchangeClientRegistration.FingerprintKey,
            It.IsAny<string?>())).ReturnsAsync(storedFingerprint);
        context.Setup(c => c.SetConfigurationAsync(It.IsAny<IOctoAdminSession>(), It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);
        return (context, session);
    }

    private static ClaimsPrincipal Principal(string? sub, string? clientId, params string[] scopes)
    {
        var claims = new List<Claim> { new("tenant_id", Tenant) };
        if (sub != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
        }

        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        claims.AddRange(scopes.Select(s => new Claim("scope", s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static McpServer Server(ClaimsPrincipal principal, IAgentTokenExchanger? exchanger,
        ITenantTokenExchanger? tenantExchanger = null, string? tenantResolution = null)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Headers.Authorization = "Bearer " + AgentToken;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var services = new ServiceCollection();
        services.AddSingleton(accessor.Object);
        services.AddSingleton(new Mock<IMcpSessionTokenStore>().Object);
        services.AddSingleton(new Mock<ISessionTokenRefresher>().Object);
        services.AddSingleton(tenantExchanger ?? new Mock<ITenantTokenExchanger>().Object);
        if (exchanger != null)
        {
            services.AddSingleton(exchanger);
        }

        if (tenantResolution != null)
        {
            var resolver = new Mock<ITenantResolutionService>();
            resolver.Setup(r => r.ResolveTenantId(It.IsAny<string?>())).Returns(tenantResolution);
            services.AddSingleton(resolver.Object);
        }

        var server = new Mock<McpServer>();
        server.Setup(s => s.Services).Returns(services.BuildServiceProvider());
        return server.Object;
    }

    private static AgentTokenExchanger Exchanger(HttpMessageHandler handler, string? secret,
        SingleHandlerFactory? factory = null) =>
        new(factory ?? new SingleHandlerFactory(handler),
            Options.Create(new McpServiceOptions
                { AuthorityUrl = "https://identity.example.com", AgentExchangeClientSecret = secret }),
            NullLogger<AgentTokenExchanger>.Instance);

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
