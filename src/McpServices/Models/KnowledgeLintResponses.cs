using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     One finding of a knowledge lint run (System.Ai record <c>KnowledgeLintFinding</c>, AB#8488). The property names are
///     those the lint pipeline emits (<c>kind</c>, <c>pageRef</c>, <c>relatedPageRef</c>, <c>detail</c>).
/// </summary>
public class KnowledgeLintFindingDto
{
    /// <summary>Category of the finding.</summary>
    [Description("Category: Contradiction, Orphan, Stale, MissingLink, UnsupportedClaim, CitationNotInSource or PlatformUpdateAvailable.")]
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Slug of the page the finding is about.</summary>
    [Description("Slug of the page the finding is about.")]
    [JsonPropertyName("pageRef")]
    public string PageRef { get; set; } = string.Empty;

    /// <summary>Slug of the second page (contradiction, missing link), if any.</summary>
    [Description("Slug of the second page of a contradiction or missing link; omitted otherwise.")]
    [JsonPropertyName("relatedPageRef")]
    public string? RelatedPageRef { get; set; }

    /// <summary>What was found, in one or two sentences.</summary>
    [Description("What was found, in one or two sentences (up to 2000 characters).")]
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Result of <c>knowledge_write_lint_report</c>.</summary>
public class KnowledgeLintReportResponse : KnowledgeResponse
{
    /// <summary>rtId of the written report.</summary>
    public string? RtId { get; set; }

    /// <summary>Name of the written report.</summary>
    public string? Name { get; set; }

    /// <summary>Number of findings written.</summary>
    public int FindingCount { get; set; }

    /// <summary>Number of findings per kind.</summary>
    public Dictionary<string, int> FindingsByKind { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Always <c>false</c>: a report written over MCP is unvalidated until its precision is measured.</summary>
    public bool IsValidated { get; set; }
}

/// <summary>AB#8780: a knowledge page for which a blueprint update brought a newer Blueprint version.</summary>
public class KnowledgePlatformUpdateDto
{
    /// <summary>Slug of the page.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    ///     <c>Adoptable</c> (unedited page, the maintenance switches it) or <c>Available</c> (edited page, the lint reports
    ///     <c>PlatformUpdateAvailable</c>).
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The version the page shows now.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>The newer Blueprint version.</summary>
    public int NewerVersion { get; set; }

    /// <summary>The blueprint id and version that brought it, e.g. <c>System.Ai.Builder-1.1.0</c>.</summary>
    public string? Blueprint { get; set; }

    /// <summary>The Blueprint version a person dismissed earlier, if any.</summary>
    public int? DismissedVersion { get; set; }
}

/// <summary>Result of <c>knowledge_list_platform_updates</c>.</summary>
public class KnowledgePlatformUpdatesResponse : KnowledgeResponse
{
    /// <summary>The pages with a newer Blueprint version, in slug order.</summary>
    public List<KnowledgePlatformUpdateDto> Updates { get; set; } = [];

    /// <summary>True when the safety cap of Blueprint versions was hit; not every page was looked at.</summary>
    public bool Truncated { get; set; }
}

/// <summary>One page of <c>knowledge_adopt_platform_updates</c>.</summary>
public class KnowledgePlatformAdoptionDto
{
    /// <summary>True when the page was switched.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Slug of the page.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The version the page showed before.</summary>
    public int FromVersion { get; set; }

    /// <summary>The Blueprint version the page shows now (or would, when skipped).</summary>
    public int ToVersion { get; set; }

    /// <summary>The blueprint id and version of that version.</summary>
    public string? Blueprint { get; set; }

    /// <summary>LinksTo associations added to follow the declared links of the version.</summary>
    public int LinksAdded { get; set; }

    /// <summary>LinksTo associations removed to follow the declared links of the version.</summary>
    public int LinksRemoved { get; set; }

    /// <summary>Declared link slugs without a page in the tenant (not linked).</summary>
    public List<string> UnresolvedLinks { get; set; } = [];

    /// <summary>Why the page was skipped (content guard, version conflict, write failure).</summary>
    public string? Reason { get; set; }
}

/// <summary>Result of <c>knowledge_adopt_platform_updates</c>.</summary>
public class KnowledgeAdoptPlatformUpdatesResponse : KnowledgeResponse
{
    /// <summary>The run reference stored on every switched page (AdoptedByRun).</summary>
    public string? RunRef { get; set; }

    /// <summary>The pages switched to a newer Blueprint version.</summary>
    public List<KnowledgePlatformAdoptionDto> Adopted { get; set; } = [];

    /// <summary>Unedited pages that could not be switched, with the reason.</summary>
    public List<KnowledgePlatformAdoptionDto> Skipped { get; set; } = [];

    /// <summary>Edited pages with a newer Blueprint version; they keep their content.</summary>
    public List<KnowledgePlatformUpdateDto> Available { get; set; } = [];

    /// <summary>True when the safety cap of Blueprint versions was hit; run again for the rest.</summary>
    public bool Truncated { get; set; }
}
