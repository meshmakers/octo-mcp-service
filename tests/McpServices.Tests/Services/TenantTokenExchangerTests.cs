using System.Net;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     HTTP-level specs for <see cref="TenantTokenExchanger" /> (AB#4338). Drives the exchanger through
///     a fake <see cref="HttpMessageHandler" /> and asserts the RFC 8693 token-exchange wire format plus
///     the 2xx-parses / non-2xx-null contract that the transparent-acquisition path relies on.
/// </summary>
public class TenantTokenExchangerTests
{
    private const string HomeToken = "home-tenant-A-access-token";
    private const string TargetTenant = "tenant-b";

    [Fact]
    public async Task ExchangeForTenantAsync_SendsTokenExchangeGrantWireFormat()
    {
        var handler = new CapturingHandler(new Canned(HttpStatusCode.OK, """
            { "access_token": "B-scoped-token", "token_type": "Bearer", "expires_in": 3600 }
            """));
        var exchanger = MakeExchanger(handler);

        await exchanger.ExchangeForTenantAsync(HomeToken, TargetTenant, CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("/connect/token");

        var body = handler.RequestBodies[0];
        body.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange");
        body.Should().Contain("subject_token=" + HomeToken);
        body.Should().Contain("subject_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aaccess_token");
        body.Should().Contain("acr_values=tenant%3A" + TargetTenant);
        body.Should().Contain("client_id=octo-mcpServices-device");
        body.Should().Contain("scope=openid+profile+email+role+octo_api");
    }

    [Fact]
    public async Task ExchangeForTenantAsync_On2xx_ParsesTokenAndExpiry()
    {
        var handler = new CapturingHandler(new Canned(HttpStatusCode.OK, """
            { "access_token": "B-scoped-token", "token_type": "Bearer", "expires_in": 3600 }
            """));
        var exchanger = MakeExchanger(handler);

        var result = await exchanger.ExchangeForTenantAsync(
            HomeToken, TargetTenant, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("B-scoped-token");
        result.RefreshToken.Should().BeNull("v1 issues no exchanged refresh token");
        result.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(3600), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ExchangeForTenantAsync_OnNon2xx_ReturnsNull()
    {
        // Identity returns 400 unauthorized_client when the user may not access the target tenant.
        var handler = new CapturingHandler(new Canned(HttpStatusCode.BadRequest, """
            { "error": "unauthorized_client" }
            """));
        var exchanger = MakeExchanger(handler);

        var result = await exchanger.ExchangeForTenantAsync(
            HomeToken, TargetTenant, CancellationToken.None);

        result.Should().BeNull("a non-2xx OAuth error must surface as null so the tool recommends 'authenticate'");
    }

    [Fact]
    public async Task ExchangeForTenantAsync_On2xxMissingAccessToken_ReturnsNull()
    {
        var handler = new CapturingHandler(new Canned(HttpStatusCode.OK, """{ "expires_in": 3600 }"""));
        var exchanger = MakeExchanger(handler);

        var result = await exchanger.ExchangeForTenantAsync(
            HomeToken, TargetTenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeForTenantAsync_WithBlankArgs_ReturnsNullWithoutHttpCall()
    {
        var handler = new CapturingHandler(new Canned(HttpStatusCode.OK, "{}"));
        var exchanger = MakeExchanger(handler);

        var noToken = await exchanger.ExchangeForTenantAsync(
            "", TargetTenant, CancellationToken.None);
        var noTenant = await exchanger.ExchangeForTenantAsync(
            HomeToken, "", CancellationToken.None);

        noToken.Should().BeNull();
        noTenant.Should().BeNull();
        handler.Requests.Should().BeEmpty("blank args must be rejected before any HTTP round-trip");
    }

    private static TenantTokenExchanger MakeExchanger(HttpMessageHandler handler)
    {
        var factory = new SingleHandlerFactory(handler);
        var options = Options.Create(new McpServiceOptions { AuthorityUrl = "https://identity.example.com" });
        return new TenantTokenExchanger(factory, options, NullLogger<TenantTokenExchanger>.Instance);
    }

    private sealed record Canned(HttpStatusCode StatusCode, string Body);

    private sealed class CapturingHandler(params Canned[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();
        private int _index;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var canned = responses[_index++];
            return new HttpResponseMessage(canned.StatusCode)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
