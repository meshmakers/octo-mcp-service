using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
///     AB#6054: the MCP endpoints are the only OctoMesh endpoints that accept agent tokens (scope <c>octo_agent</c>).
///     <see cref="OctoMcpEndpoints.MapOctoMcpEndpoints" /> marks both <c>MapMcp</c> routes with
///     <see cref="IAllowAgentTokens" />; <c>UseOctoTenantAuthorization()</c> refuses an agent token on every other
///     endpoint of this service with <c>403 AgentTokenNotAccepted</c>.
/// </summary>
public class AgentTokenEndpointTests
{
    private const string Tenant = "tenant-a";
    private const string StubControllerRoute = "/system/v1/diagnostics/stub";

    private static async Task<IHost> StartHostAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddOctoTenantIdRouteConstraint();
                    services.Configure<OctoSystemConfiguration>(o => o.SystemTenantId = "octosystem");
                    services.AddOctoTenantAuthorization(new ConfigurationBuilder().Build());
                    services.AddAuthentication(TestBearerHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, _ => { });
                    services.AddAuthorization(options =>
                        McpAuthorizationPolicy.AddMcpTransportPolicy(options, ["octo_api"]));
                    services.AddMcpServer().WithHttpTransport();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseOctoAgentTokenRefusal();
                    app.UseAuthorization();
                    app.UseOctoTenantAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapOctoMcpEndpoints();
                        // Stands in for the controllers of this service (diagnostics): the platform-scope policy
                        // McpTransportPolicy, which an agent token fails before the refusal of the tenant gate runs.
                        endpoints.MapGet(StubControllerRoute, () => Results.Ok("diagnostics"))
                            .RequireAuthorization(McpAuthorizationPolicy.PolicyName);
                    });
                }))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpRequestMessage Initialize(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task BothMcpEndpoints_AndOnlyThey_AreMarkedAllowAgentTokens()
    {
        using var host = await StartHostAsync();

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .ToList();
        // MapMcp maps a route group (the streamable HTTP endpoint and its sub-routes) under each prefix.
        var marked = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAgentTokens>() != null)
            .Select(e => e.RoutePattern.RawText!).ToList();
        var prefixes = new[] { OctoMcpEndpoints.TenantRoute, OctoMcpEndpoints.TenantlessRoute };

        marked.Should().NotBeEmpty();
        marked.Should().OnlyContain(r => prefixes.Any(prefix => r == prefix || r.StartsWith(prefix + "/")));
        prefixes.Should().OnlyContain(prefix => marked.Any(r => r == prefix || r.StartsWith(prefix + "/")));
        endpoints.Single(e => e.RoutePattern.RawText == StubControllerRoute).Metadata
            .GetMetadata<IAllowAgentTokens>().Should().BeNull();
    }

    [Theory]
    [InlineData("/" + Tenant + "/mcp")]
    [InlineData("/mcp")]
    public async Task AgentToken_IsAcceptedByTheMcpEndpoints(string path)
    {
        using var host = await StartHostAsync();

        var response = await host.GetTestClient().SendAsync(Initialize(path, TestBearerHandler.AgentToken),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AgentToken_IsRefusedByEveryOtherEndpoint_WithAgentTokenNotAccepted()
    {
        using var host = await StartHostAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, StubControllerRoute);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestBearerHandler.AgentToken);
        var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().Should().Be(AgentTokens.NotAcceptedErrorCode);
    }

    [Fact]
    public void NoTypeOrMemberOfThisService_IsMarkedAllowAgentTokens()
    {
        var assembly = typeof(OctoMcpEndpoints).Assembly;
        var marked = assembly.GetTypes()
            .SelectMany(t => new System.Reflection.MemberInfo[] { t }.Concat(t.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly)))
            .Where(m => m.GetCustomAttributes(typeof(AllowAgentTokensAttribute), true).Length > 0)
            .Select(m => m.Name)
            .ToList();

        marked.Should().BeEmpty("only the two MapMcp routes opt in, through .AllowAgentTokens() in OctoMcpEndpoints");
    }

    [Fact]
    public async Task PlatformToken_IsNotAffected()
    {
        using var host = await StartHostAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, StubControllerRoute);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestBearerHandler.ApiToken);
        var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Client-credentials principals labelled <c>Bearer</c>: the "token" says whether it is an agent's.</summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Bearer";
        public const string AgentToken = "agent";
        public const string ApiToken = "api";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var scope = header["Bearer ".Length..] == AgentToken ? "octo_agent" : "octo_api";
            var identity = new ClaimsIdentity(
            [
                new Claim("client_id", "agent-builder"), new Claim("tenant_id", Tenant), new Claim("scope", scope)
            ], "Bearer");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
