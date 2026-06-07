using System.Net;
using System.Net.Http;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace McpServices.Tests.Resources;

/// <summary>
///     Surface tests for <see cref="KnowledgeResources" /> — covers URI dispatch and the
///     pre-DB validation paths (blank rtId, malformed ObjectId). End-to-end content rendering
///     for ClaudeMd / Url / McpResource / RagDoc is exercised in the worker integration tests
///     against real AiKnowledgeSource entities; here we keep the surface narrow and
///     mock-driven.
/// </summary>
public class KnowledgeResourcesTests : TestBase
{
    public KnowledgeResourcesTests()
    {
        // The fixture base does not register IHttpClientFactory or ILoggerFactory by default —
        // the knowledge fetch path needs both. Register no-op implementations sufficient for
        // the URL-kind branch (which isn't exercised in these surface tests but the DI
        // resolution must still succeed for any Url-kind future test).
        TestServiceProvider.RegisterService<IHttpClientFactory>(new StubHttpClientFactory());
        TestServiceProvider.RegisterService<ILoggerFactory>(NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task GetKnowledgeSourceAsync_BlankRtId_RendersError()
    {
        var markdown = await KnowledgeResources.GetKnowledgeSourceAsync(
            MockServer.Object, "test-tenant", "   ");

        markdown.Should().Contain("_Error: rtId is required._");
        // No tenant resolution should happen — short-circuit before any DI lookup.
        MockTenantResolution.Verify(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task GetKnowledgeSourceAsync_MalformedRtId_RendersValidationError()
    {
        // The URI template hands us the rtId segment verbatim; 24-char hex is the Mongo
        // ObjectId invariant the runtime engine enforces. Anything else must not reach the
        // repository — render a clean validation error instead.
        var markdown = await KnowledgeResources.GetKnowledgeSourceAsync(
            MockServer.Object, "test-tenant", "not-a-valid-object-id");

        markdown.Should().Contain("Invalid rtId");
        markdown.Should().Contain("not-a-valid-object-id");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
