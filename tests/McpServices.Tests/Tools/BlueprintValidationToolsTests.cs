using System.IO.Compression;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9371: validate_blueprint stages the inline draft as a zip, calls the asset repository's blueprint validation
///     (SDK ValidateBlueprintAsync) and maps the findings with their stable codes; nothing is installed.
/// </summary>
public class BlueprintValidationToolsTests : ToolTestBase
{
    public BlueprintValidationToolsTests()
    {
        GivenAuthenticated();
    }

    private static List<CkDraftFileDto> Draft() =>
    [
        new() { Name = "blueprint.yaml", Content = "blueprintId: Draft-1.0.0\nseedDataPath: seed-data/entities.yaml\n" },
        new() { Name = "seed-data/entities.yaml", Content = "entities: []\n" }
    ];

    [Fact]
    public async Task ValidateBlueprint_InlineFiles_PostsAZipOfTheFiles_AndDeletesItAfterwards()
    {
        string? postedPath = null;
        List<string>? entries = null;
        MockAssetClient.Setup(c => c.ValidateBlueprintAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) =>
            {
                postedPath = path;
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Select(e => e.FullName).ToList();
            })
            .ReturnsAsync(new BlueprintDraftValidationResponseDto { IsValid = true, BlueprintId = "Draft-1.0.0" });

        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object, files: Draft());

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeTrue();
        result.BlueprintId.Should().Be("Draft-1.0.0");
        result.TenantId.Should().Be(DefaultTenantId);
        entries.Should().BeEquivalentTo("blueprint.yaml", "seed-data/entities.yaml");
        File.Exists(postedPath).Should().BeFalse("an inline draft lives only for the call");
        MockAssetClient.Verify(c => c.ApplyBlueprintAsync(It.IsAny<string>(), It.IsAny<BlueprintApplyRequestDto>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidateBlueprint_Invalid_ReturnsTheFindingsWithCodes()
    {
        MockAssetClient.Setup(c => c.ValidateBlueprintAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync(new BlueprintDraftValidationResponseDto
            {
                IsValid = false, BlueprintId = "Draft-1.0.0", TotalFindings = 1,
                Findings =
                [
                    new BlueprintDraftFindingDto
                    {
                        Code = 163, Level = "Error", File = "blueprint.yaml", ModelId = "Acme.Unknown",
                        Message = "CK model dependency 'Acme.Unknown' in range '[7.0,8.0)' resolves in none of the CK model catalogs (all)"
                    }
                ]
            });

        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object, files: Draft());

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeFalse();
        result.Message.Should().Contain("1 error");
        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be(163);
        finding.ModelId.Should().Be("Acme.Unknown");
    }

    [Fact]
    public void Map_BoundsTheFindings()
    {
        var dto = new BlueprintDraftValidationResponseDto
        {
            IsValid = true, TotalFindings = 150,
            Findings = Enumerable.Range(0, 150)
                .Select(_ => new BlueprintDraftFindingDto { Code = 159, Level = "Warning" }).ToList()
        };

        var result = BlueprintValidationTools.Map(dto, DefaultTenantId);

        result.Findings.Should().HaveCount(BlueprintValidationTools.MaxFindings);
        result.Truncated.Should().BeTrue();
        result.TotalFindings.Should().Be(150);
    }

    [Fact]
    public void Map_TruncatedInvalidDraft_SaysAtLeast_AndNamesTheTotal()
    {
        // The server already cut the list: the error count of the page is a lower bound (Kassandra round 1, finding 4).
        var dto = new BlueprintDraftValidationResponseDto
        {
            IsValid = false, TotalFindings = 300, Truncated = true,
            Findings = Enumerable.Range(0, 20).Select(_ => new BlueprintDraftFindingDto { Code = 162, Level = "Error" }).ToList()
        };

        var result = BlueprintValidationTools.Map(dto, DefaultTenantId);

        result.Message.Should().Be("Blueprint draft has at least 20 error(s); the finding list is cut, 300 finding(s) in total.");
    }

    [Fact]
    public void Map_CompleteInvalidDraft_CountsTheErrors()
    {
        var dto = new BlueprintDraftValidationResponseDto
        {
            IsValid = false, TotalFindings = 3,
            Findings =
            [
                new BlueprintDraftFindingDto { Code = 162, Level = "Error" },
                new BlueprintDraftFindingDto { Code = 163, Level = "FatalError" },
                new BlueprintDraftFindingDto { Code = 159, Level = "Warning" }
            ]
        };

        BlueprintValidationTools.Map(dto, DefaultTenantId).Message.Should().Be("Blueprint draft has 2 error(s).");
    }

    [Fact]
    public async Task ValidateBlueprint_InvalidFileName_HintNamesTheBlueprintFolder()
    {
        // Kassandra round 1, finding 5: the hint must not send a blueprint author to ConstructionKit/.
        var files = new List<CkDraftFileDto> { new() { Name = "../blueprint.yaml", Content = "x" } };

        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object, files: files);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("inside the blueprint folder").And.NotContain("ConstructionKit/");
        MockAssetClient.Verify(c => c.ValidateBlueprintAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateBlueprint_Unauthenticated_ReturnsError_AndCallsNothing()
    {
        GivenUnauthenticated();

        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object, files: Draft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAssetClient.Verify(c => c.ValidateBlueprintAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateBlueprint_NoDraft_ReturnsValidationError()
    {
        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("files");
        MockAssetClient.Verify(c => c.ValidateBlueprintAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateBlueprint_InlineDraftTooLarge_IsRefusedBeforeAnyCall()
    {
        var files = new List<CkDraftFileDto>
        {
            new() { Name = "blueprint.yaml", Content = new string('x', CkDraftInlinePackager.MaxInlineBytes + 1) }
        };

        var result = await BlueprintValidationTools.ValidateBlueprint(MockServer.Object, files: files);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(CkDraftInlinePackager.InlineDraftTooLargeCode);
        MockAssetClient.Verify(c => c.ValidateBlueprintAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Tool_IsLowRisk_AndAReadingTool()
    {
        var method = typeof(BlueprintValidationTools).GetMethod(nameof(BlueprintValidationTools.ValidateBlueprint))!;
        var risk = method.GetCustomAttributes(typeof(McpRiskAttribute), false).Cast<McpRiskAttribute>().Single();
        risk.Level.Should().Be(McpRiskLevel.Low);
        Meshmakers.Octo.Backend.McpServices.Services.Agents.AgentMandateGuard.ReadingTools.Should()
            .Contain("validate_blueprint");
    }
}
