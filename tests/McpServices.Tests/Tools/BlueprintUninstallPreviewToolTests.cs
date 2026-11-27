using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9034: preview_blueprint_uninstall shows what an uninstall would delete and keep, through the SDK preview of
///     AB#8966 only - it never calls the real uninstall, also not against an old server.
/// </summary>
public class BlueprintUninstallPreviewToolTests : ToolTestBase
{
    public BlueprintUninstallPreviewToolTests()
    {
        GivenAuthenticated();
    }

    private static BlueprintUninstallResultDto Preview(int deleted = 2, int kept = 1, bool dryRun = true) => new()
    {
        Success = true,
        DryRun = dryRun,
        UninstalledBlueprintId = "System.Ai.Builder-1.0.0",
        EntitiesDeleted = deleted,
        DeletedEntities = Enumerable.Range(0, deleted)
            .Select(i => new BlueprintUninstallDeletedEntityDto { Key = $"d{i}", TenantOwned = i == 0 }).ToList(),
        TenantOwnedKept = Enumerable.Range(0, kept)
            .Select(i => new BlueprintTenantOwnedKeptEntityDto { Key = $"k{i}", Reason = "Edited" }).ToList()
    };

    private void VerifyNeverUninstalled() =>
        MockAssetClient.Verify(c => c.UninstallBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);

    [Fact]
    public async Task Preview_MapsDeletedAndKept_AndNeverUninstalls()
    {
        MockAssetClient.Setup(c => c.PreviewUninstallBlueprintAsync(DefaultTenantId, "System.Ai.Builder", true))
            .ReturnsAsync(Preview());

        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, "System.Ai.Builder", cascade: true);

        result.IsSuccess.Should().BeTrue();
        result.DeletedCount.Should().Be(2);
        result.KeptCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
        result.Result!.DeletedEntities.Select(e => (e.Key, e.TenantOwned)).Should().Equal(("d0", true), ("d1", false));
        result.Result.TenantOwnedKept.Single().Reason.Should().Be("Edited");
        result.Message.Should().Contain("would delete 2").And.Contain("keep 1").And.Contain("Nothing changed");
        VerifyNeverUninstalled();
    }

    [Fact]
    public async Task Preview_LongLists_AreCutButCountsStayComplete()
    {
        MockAssetClient.Setup(c => c.PreviewUninstallBlueprintAsync(DefaultTenantId, "Big", false))
            .ReturnsAsync(Preview(deleted: 500, kept: 3));

        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, "Big");

        result.DeletedCount.Should().Be(500);
        result.Result!.DeletedEntities.Should().HaveCount(BlueprintTools.MaxPreviewEntities);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_OldServerWithoutTheRoute_ClearErrorAndNoUninstall()
    {
        MockAssetClient.Setup(c => c.PreviewUninstallBlueprintAsync(DefaultTenantId, "System.Ai.Builder", false))
            .ThrowsAsync(new ServiceClientResultException("The call was not successful: $NotFound", HttpStatusCode.NotFound));

        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, "System.Ai.Builder");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BlueprintTools.UninstallPreviewUnsupported);
        result.ErrorMessage.Should().Contain("Nothing was uninstalled");
        VerifyNeverUninstalled();
    }

    [Fact]
    public async Task Preview_NotInstalled_KeepsTheServiceMessage()
    {
        const string body = """{"message":"Blueprint 'Nope' is not installed"}""";
        MockAssetClient.Setup(c => c.PreviewUninstallBlueprintAsync(DefaultTenantId, "Nope", false))
            .ThrowsAsync(new ServiceClientResultException(body, HttpStatusCode.NotFound) { ResponseContent = body });

        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, "Nope");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().NotBe(BlueprintTools.UninstallPreviewUnsupported);
        result.ErrorMessage.Should().Contain("not installed");
    }

    [Fact]
    public async Task Preview_AnswerWithoutDryRun_IsAnErrorNotAResult()
    {
        MockAssetClient.Setup(c => c.PreviewUninstallBlueprintAsync(DefaultTenantId, "System.Ai.Builder", false))
            .ReturnsAsync(Preview(dryRun: false));

        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, "System.Ai.Builder");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BlueprintTools.UninstallPreviewNotDryRun);
        result.Result.Should().BeNull();
    }

    [Fact]
    public async Task Preview_WithoutName_Refuses()
    {
        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, " ");

        result.IsSuccess.Should().BeFalse();
        MockAssetClient.Verify(c => c.PreviewUninstallBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("x/../../updates/apply#")]
    [InlineData("name?cascade=true")]
    [InlineData("na%2Fme")]
    public async Task Preview_NameThatIsNoSinglePathSegment_IsRefused_AndNoRouteIsCalled(string name)
    {
        // The SDK interpolates the name into the POST route unescaped (Kassandra round 1, finding 1).
        var result = await BlueprintTools.PreviewBlueprintUninstall(MockServer.Object, name);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BlueprintTools.InvalidRouteSegment);
        result.ErrorMessage.Should().Contain("blueprintName");
        MockAssetClient.Verify(c => c.PreviewUninstallBlueprintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        VerifyNeverUninstalled();
    }

    [Fact]
    public void Preview_IsLowAndOnTheReadingAllowList()
    {
        AgentMandateGuard.IsReading("preview_blueprint_uninstall", McpRiskLevel.Low).Should().BeTrue();
        AgentMandateGuard.ReadingTools.Should().NotContain("uninstall_blueprint");
    }
}
