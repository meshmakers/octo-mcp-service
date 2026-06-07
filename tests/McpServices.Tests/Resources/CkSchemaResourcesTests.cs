using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Resources;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Resources;

/// <summary>
///     Surface tests for <see cref="CkSchemaResources" /> — covers URI dispatch (system vs domain),
///     tenant resolution, empty-model rendering, and the StartsWith("System") scope filter.
///     Full schema-rendering content is exercised end-to-end in the worker integration tests
///     where a real CK cache is available; here we keep the test surface narrow and mock-driven.
/// </summary>
public class CkSchemaResourcesTests : TestBase
{
    [Fact]
    public async Task GetSystemSchemaAsync_NoModelsLoaded_RendersSystemHeaderAndPlaceholder()
    {
        MockCkCacheService.Setup(c => c.GetCkModelIds("test-tenant"))
            .Returns(new List<CkModelId>());

        var markdown = await CkSchemaResources.GetSystemSchemaAsync(MockServer.Object, "test-tenant");

        markdown.Should().StartWith("# CK Schema — System");
        markdown.Should().Contain("Tenant: `test-tenant`");
        markdown.Should().Contain("_No System models loaded for this tenant._");
    }

    [Fact]
    public async Task GetDomainSchemaAsync_OnlySystemModelsLoaded_RendersDomainHeaderAndEmpty()
    {
        // System.Ai-3 is a System model so it must be EXCLUDED from the domain scope.
        MockCkCacheService.Setup(c => c.GetCkModelIds("test-tenant"))
            .Returns(new List<CkModelId> { new("System.Ai-3") });

        var markdown = await CkSchemaResources.GetDomainSchemaAsync(MockServer.Object, "test-tenant");

        markdown.Should().StartWith("# CK Schema — Domain");
        markdown.Should().Contain("Models: 0");
        markdown.Should().Contain("_No domain models loaded for this tenant._");
        markdown.Should().NotContain("System.Ai");
    }

    [Fact]
    public async Task GetSystemSchemaAsync_BlankTenant_RendersError()
    {
        // The MCP URI template enforces a tenantId segment but the SDK passes the raw string,
        // so guard against the empty case at the method boundary.
        var markdown = await CkSchemaResources.GetSystemSchemaAsync(MockServer.Object, "   ");

        markdown.Should().Contain("_Error: tenantId is required._");
        MockCkCacheService.Verify(c => c.GetCkModelIds(It.IsAny<string>()), Times.Never);
    }
}
