using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Xunit;

namespace McpServices.Tests.Services.Knowledge;

/// <summary>
///     AB#8565: the content hash of a knowledge page is part of a contract with octo-cli. The command
///     <c>SetKnowledgePageStatus</c> (octo-cli <c>KnowledgePageContent</c>) recomputes it from the GraphQL page to write a
///     status version outside MCP. Both repositories pin this golden value: a change of <c>KnowledgePages.Render</c>
///     must change octo-cli's port and test in the same step.
/// </summary>
public class KnowledgeContentHashGoldenTests
{
    internal const string GoldenHash = "c05d1afbca04a6e2fe064fda7056c0a10eb67657386299a21477e7d19e068b45";

    [Fact]
    public void ContentHash_OfTheGoldenPage_IsPinned()
    {
        var content = new KnowledgeContent("Golden page", 2, "Zebrafisch über alles.",
            [
                new KnowledgeSectionDto { Heading = "Steps", SectionContent = "Do A.\nThen B.", SortOrder = 1 },
                new KnowledgeSectionDto { Heading = "Intro", SectionContent = "Why.", SortOrder = 0 }
            ],
            [
                new KnowledgeCitationDto
                {
                    SectionRef = "Intro", SourceQuote = "quote", SourceAnchor = "p.3", SourceRtId = "672000000000000000000101"
                },
                new KnowledgeCitationDto { SectionRef = "Steps", SourceQuote = "q2" }
            ],
            [new KnowledgeHallucinationFlagDto { Claim = "c", Reason = "r" }],
            KnowledgePages.StatusVerified);

        var write = KnowledgePages.BuildWrite(null, "golden-page", content, 2, "user:x", "golden", null, null,
            new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));

        write.Page.GetAttributeStringValueOrDefault("ContentHash").Should().Be(GoldenHash);
    }
}
