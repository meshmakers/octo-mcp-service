using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     HTTP-level specs for <see cref="RuntimeSecretInventoryClient" /> (AB#5543): request shape (endpoint, bearer,
///     variables, optional selections) and the mapping of GraphQL responses to typed outcomes.
/// </summary>
public class RuntimeSecretInventoryClientTests
{
    private const string Body = """
        {"data":{"secrets":{
          "inventory":{"totalCount":2,"pageInfo":{"hasNextPage":true,"endCursor":"c1"},"items":[
            {"ckTypeId":"System.Communication/SftpConfiguration","rtId":"507f1f77bcf86cd799439011",
             "rtWellKnownName":null,"displayName":"Upload","attributePath":"password","attributeName":"Password",
             "required":true,"form":"ENC_V2","keyId":"k2","setAt":"2026-10-06T08:00:00Z","needsReEntry":false,
             "usedBy":[{"dataFlowRtId":null,"dataFlowName":null,"pipelineRtId":"507f1f77bcf86cd799439020",
                        "pipelineName":"Upload","nodePath":"transformations[0]","match":"EXACT"}]}]},
          "summary":{"total":2,"notSet":1,"plaintext":0,"encV1":0,"encV2":1,"keyMissing":0,"corrupt":0,
                     "needsReEntry":1,"encV2ByKeyId":[{"keyId":"k2","count":1}]}}}}
        """;

    [Fact]
    public async Task QueryAsync_Success_ParsesInventoryAndSummary_AndSendsVariables()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, Body);
        var client = MakeClient(handler);

        var result = await client.QueryAsync("fake-token", "tenant-a", new SecretInventoryRequest
        {
            First = 10, After = "c0", Forms = ["KEY_MISSING"], NeedsReEntry = true, Search = "x",
            IncludeSummary = true, IncludeUsedBy = true
        }, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.Succeeded, result.ErrorMessage);
        result.Inventory!.TotalCount.Should().Be(2);
        result.Inventory.PageInfo!.EndCursor.Should().Be("c1");
        var item = result.Inventory.Items.Should().ContainSingle().Subject;
        item.Form.Should().Be("ENC_V2");
        item.SetAt.Should().Be(new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));
        item.UsedBy.Should().ContainSingle().Which.Match.Should().Be("EXACT");
        result.Summary!.EncV2ByKeyId.Should().ContainSingle().Which.Count.Should().Be(1);

        handler.Request!.RequestUri!.ToString().Should().Be("https://asset.local/tenants/tenant-a/graphQl");
        handler.Request.Headers.Authorization!.Parameter.Should().Be("fake-token");
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var variables = doc.RootElement.GetProperty("variables");
        variables.GetProperty("first").GetInt32().Should().Be(10);
        variables.GetProperty("after").GetString().Should().Be("c0");
        variables.GetProperty("forms")[0].GetString().Should().Be("KEY_MISSING");
        variables.GetProperty("needsReEntry").GetBoolean().Should().BeTrue();
        var query = doc.RootElement.GetProperty("query").GetString()!;
        query.Should().Contain("summary {").And.Contain("usedBy {");
    }

    [Fact]
    public void BuildQuery_WithoutOptions_SelectsNoSummaryNoUsedBy_AndIsBalanced()
    {
        var query = RuntimeSecretInventoryClient.BuildQuery(false, false);

        query.Should().NotContain("summary").And.NotContain("usedBy");
        query.Count(c => c == '{').Should().Be(query.Count(c => c == '}'));
        RuntimeSecretInventoryClient.BuildQuery(true, true).Count(c => c == '{')
            .Should().Be(RuntimeSecretInventoryClient.BuildQuery(true, true).Count(c => c == '}'));
    }

    [Fact]
    public async Task QueryAsync_ForbiddenCode_ReturnsForbidden()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, """
            {"errors":[{"message":"The secrets overview requires the 'AdminPanelManagement' role.",
                        "extensions":{"code":"Forbidden"}}],"data":{"secrets":null}}
            """);

        var result = await MakeClient(handler).QueryAsync("t", "tenant-a", new SecretInventoryRequest(),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.Forbidden);
        result.ErrorMessage.Should().Contain("AdminPanelManagement").And.Contain("tenant-a");
    }

    [Fact]
    public async Task QueryAsync_OtherGraphQlError_ReturnsUnexpectedError()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, """{"errors":[{"message":"Cannot query field"}]}""");

        var result = await MakeClient(handler).QueryAsync("t", "tenant-a", new SecretInventoryRequest(),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.UnexpectedError);
        result.ErrorMessage.Should().Contain("Cannot query field");
    }

    [Fact]
    public async Task QueryAsync_401_ReturnsUnauthorised()
    {
        var result = await MakeClient(new CannedHandler(HttpStatusCode.Unauthorized, ""))
            .QueryAsync("t", "tenant-a", new SecretInventoryRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.Unauthorised);
        result.ErrorMessage.Should().Contain("401");
    }

    [Fact]
    public async Task QueryAsync_502_ReturnsNotReachable()
    {
        var result = await MakeClient(new CannedHandler(HttpStatusCode.BadGateway, "Bad gateway"))
            .QueryAsync("t", "tenant-a", new SecretInventoryRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.NotReachable);
        result.ErrorMessage.Should().Contain("502");
    }

    [Fact]
    public async Task QueryAsync_NetworkFailure_ReturnsNotReachable()
    {
        var result = await MakeClient(new ThrowingHandler())
            .QueryAsync("t", "tenant-a", new SecretInventoryRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.NotReachable);
        result.ErrorMessage.Should().Contain("unreachable");
    }

    [Fact]
    public async Task QueryAsync_NoAssetUrl_ReturnsNotReachable()
    {
        var client = new RuntimeSecretInventoryClient(new SingleHandlerFactory(new ThrowingHandler()),
            Microsoft.Extensions.Options.Options.Create(new OctoServiceUrlOptions()),
            NullLogger<RuntimeSecretInventoryClient>.Instance);

        var result = await client.QueryAsync("t", "tenant-a", new SecretInventoryRequest(),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SecretInventoryQueryOutcome.NotReachable);
        result.ErrorMessage.Should().Contain("AssetServiceUrl");
    }

    private static RuntimeSecretInventoryClient MakeClient(HttpMessageHandler handler)
    {
        return new RuntimeSecretInventoryClient(new SingleHandlerFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new OctoServiceUrlOptions { AssetServiceUrl = "https://asset.local/" }),
            NullLogger<RuntimeSecretInventoryClient>.Instance);
    }

    private sealed class CannedHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated DNS failure");
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
