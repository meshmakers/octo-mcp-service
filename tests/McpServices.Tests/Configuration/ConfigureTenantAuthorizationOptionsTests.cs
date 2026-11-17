using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
///     AB#9188: the MCP service trusts the AI service's MCP service client (<c>octo-ai-adapter</c>) for cross-tenant
///     service calls by default - one explicit allow-list entry, never a blanket exemption. The HTTP tests run the real
///     transport tenant gate (<c>UseOctoTenantAuthorization</c>, octo-common-services) with the service's wiring
///     (<see cref="AiServiceClientTenantGuard" /> in front): a system-tenant service token of <c>octo-ai-adapter</c> reaches
///     another tenant's route; any other client's does not, nor a client of the same id created in another tenant.
/// </summary>
public class ConfigureTenantAuthorizationOptionsTests
{
    private const string SystemTenant = "octosystem";
    private const string OtherTenant = "m1-e2e";

    private static TenantAuthorizationOptions PostConfigure(string? aiServiceClientId,
        params string[] configuredEntries)
    {
        var options = new TenantAuthorizationOptions();
        foreach (var entry in configuredEntries)
        {
            options.CrossTenantServiceClientIds.Add(entry);
        }

        var mcpOptions = new McpServiceOptions();
        if (aiServiceClientId != null)
        {
            mcpOptions.AiServiceClientId = aiServiceClientId;
        }

        new ConfigureTenantAuthorizationOptions(Microsoft.Extensions.Options.Options.Create(mcpOptions))
            .PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);
        return options;
    }

    [Fact]
    public void Default_ListsExactlyTheAiServiceClient()
    {
        var options = PostConfigure(aiServiceClientId: null);

        options.CrossTenantServiceClientIds.Should().Equal(McpServiceOptions.DefaultAiServiceClientId);
        options.IsCrossTenantServiceClient("octo-ai-adapter").Should().BeTrue();
        options.IsCrossTenantServiceClient("octo-ai-adapter-2").Should().BeFalse("an exact id, not a prefix");
        options.IsCrossTenantServiceClient("octo-pipeline-sa-0123").Should().BeFalse();
    }

    [Fact]
    public void OperatorEntries_StayAndTheIdIsNotAddedTwice()
    {
        var options = PostConfigure(aiServiceClientId: null, "custom-worker", " OCTO-AI-ADAPTER ");

        options.CrossTenantServiceClientIds.Should().Equal("custom-worker", " OCTO-AI-ADAPTER ");
    }

    [Fact]
    public void EmptyClientId_AddsNothing()
    {
        PostConfigure(aiServiceClientId: "  ").CrossTenantServiceClientIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("octo-*")]
    public void Pattern_IsRefused(string clientId)
    {
        var act = () => PostConfigure(clientId);

        act.Should().Throw<OptionsValidationException>().WithMessage("*exact client id*");
    }

    [Fact]
    public async Task AiServiceClient_WithASystemTenantToken_ReachesAnotherTenantsRoute()
    {
        using var host = await StartHostAsync();

        var response = await SendAsync(host, OtherTenant, clientId: "octo-ai-adapter", tokenTenant: SystemTenant);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherClient_WithASystemTenantToken_IsRefusedOnAnotherTenantsRoute()
    {
        using var host = await StartHostAsync();

        var response = await SendAsync(host, OtherTenant, clientId: "some-other-client", tokenTenant: SystemTenant);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SameClientIdCreatedInAnotherTenant_IsRefusedOnAForeignRoute()
    {
        // Client ids are unique per tenant only: a tenant administrator can create "octo-ai-adapter" in tenant-a.
        using var host = await StartHostAsync();

        var response = await SendAsync(host, OtherTenant, clientId: "OCTO-AI-ADAPTER", tokenTenant: "tenant-a");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the trust is bound to a token of the system tenant");
    }

    [Fact]
    public async Task CaseVariantOfTheClientId_FromTheSystemTenant_IsRefusedOnAForeignRoute()
    {
        // The shared allow-list matches case-insensitively, the trusted pair of the in-tool gate (AB#9189) exactly:
        // the route gate must not admit what the tools would refuse.
        using var host = await StartHostAsync();

        var response = await SendAsync(host, OtherTenant, clientId: "OCTO-AI-ADAPTER", tokenTenant: SystemTenant);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SameClientIdCreatedInAnotherTenant_StillWorksOnItsOwnRoute()
    {
        using var host = await StartHostAsync();

        var response = await SendAsync(host, "tenant-a", clientId: "octo-ai-adapter", tokenTenant: "tenant-a");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a tenant's own client keeps working in its tenant");
    }

    [Fact]
    public async Task AiServiceClient_IsRefused_WhenTheDefaultIsSwitchedOff()
    {
        using var host = await StartHostAsync(aiServiceClientId: string.Empty);

        var response = await SendAsync(host, OtherTenant, clientId: "octo-ai-adapter", tokenTenant: SystemTenant);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the trust comes from the default entry, nothing else");
    }

    [Fact]
    public async Task Pattern_StopsTheStart()
    {
        using var host = BuildHost(aiServiceClientId: "*");

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string routeTenant, string clientId,
        string tokenTenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{routeTenant}/mcp");
        // The gate only looks at bearer requests; the test scheme below builds the principal from these headers.
        request.Headers.Add("Authorization", "Bearer test-token");
        request.Headers.Add(TestServiceTokenHandler.ClientIdHeader, clientId);
        request.Headers.Add(TestServiceTokenHandler.TenantIdHeader, tokenTenant);
        return await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A host with the service's tenant authorization wiring from <c>Program.cs</c>: <c>AddOctoTenantAuthorization</c>
    ///     (section <c>TenantAuthorization</c>, empty here as on a cluster without values), the AB#9188 post-configuration and
    ///     <c>UseOctoTenantAuthorization</c> after authentication, in front of a stand-in <c>/{tenantId}/mcp</c> endpoint.
    /// </summary>
    private static async Task<IHost> StartHostAsync(string? aiServiceClientId = null)
    {
        var host = BuildHost(aiServiceClientId);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static IHost BuildHost(string? aiServiceClientId)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.Configure<McpServiceOptions>(o =>
                    {
                        if (aiServiceClientId != null)
                        {
                            o.AiServiceClientId = aiServiceClientId;
                        }
                    });
                    services.Configure<OctoSystemConfiguration>(o => o.SystemTenantId = SystemTenant);
                    services.AddOctoTenantAuthorization(configuration);
                    services.AddSingleton<IPostConfigureOptions<TenantAuthorizationOptions>,
                        ConfigureTenantAuthorizationOptions>();
                    services.AddOptions<TenantAuthorizationOptions>().ValidateOnStart();
                    services.AddAuthentication(TestServiceTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestServiceTokenHandler>(TestServiceTokenHandler.SchemeName,
                            _ => { });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseMiddleware<AiServiceClientTenantGuard>();
                    app.UseOctoTenantAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapPost("/{tenantId}/mcp", () => Results.Ok()));
                }))
            .Build();
        return host;
    }

    /// <summary>
    ///     Builds a client-credentials principal (no <c>sub</c>) labelled <c>Bearer</c>, as <c>ConfigureJwtBearerOptions</c>
    ///     labels a validated JWT, so the tenant gate runs its service-token path.
    /// </summary>
    private sealed class TestServiceTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestServiceToken";
        public const string ClientIdHeader = "X-Test-Client-Id";
        public const string TenantIdHeader = "X-Test-Tenant-Id";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new("client_id", Request.Headers[ClientIdHeader].ToString()),
                new("tenant_id", Request.Headers[TenantIdHeader].ToString()),
                new("scope", "octo_api")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
