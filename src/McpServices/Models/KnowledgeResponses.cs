using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     One section of a knowledge page. The property names are those of the KnowledgeCapture prototype's generation
///     JSON (<c>heading</c>, <c>sectionContent</c>, <c>sortOrder</c>), so its prompts keep working (AB#8484).
/// </summary>
public class KnowledgeSectionDto
{
    /// <summary>Section heading.</summary>
    [Description("Section heading, e.g. 'Summary', 'Steps', 'Pitfalls'.")]
    [JsonPropertyName("heading")]
    public string Heading { get; set; } = string.Empty;

    /// <summary>Section content (markdown).</summary>
    [Description("Section content in markdown.")]
    [JsonPropertyName("sectionContent")]
    public string SectionContent { get; set; } = string.Empty;

    /// <summary>Order of the section in the page.</summary>
    [Description("Order of the section in the page (0 first).")]
    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

/// <summary>A quote-level source attribution (prototype <c>Citation</c>).</summary>
public class KnowledgeCitationDto
{
    /// <summary>Heading of the section the citation supports.</summary>
    [Description("Heading of the section this citation supports.")]
    [JsonPropertyName("sectionRef")]
    public string SectionRef { get; set; } = string.Empty;

    /// <summary>Word-for-word quote from the source.</summary>
    [Description("Word-for-word quote from the source.")]
    [JsonPropertyName("sourceQuote")]
    public string SourceQuote { get; set; } = string.Empty;

    /// <summary>Optional anchor in the source (commit, work item, page, timestamp).</summary>
    [Description("Optional anchor in the source: commit hash, work item id, page number, timestamp.")]
    [JsonPropertyName("sourceAnchor")]
    public string? SourceAnchor { get; set; }

    /// <summary>RtId of the cited SourceArtifact or KnowledgePage.</summary>
    [Description("RtId of the cited SourceArtifact (or knowledge page). SourceArtifacts are linked with GeneratedFrom.")]
    [JsonPropertyName("sourceRtId")]
    public string? SourceRtId { get; set; }
}

/// <summary>A claim the verification pass could not ground (prototype <c>HallucinationFlag</c>).</summary>
public class KnowledgeHallucinationFlagDto
{
    /// <summary>The unsupported claim.</summary>
    [Description("The claim that could not be grounded in the sources.")]
    [JsonPropertyName("claim")]
    public string Claim { get; set; } = string.Empty;

    /// <summary>Why it was flagged.</summary>
    [Description("One-sentence reason why the claim was flagged.")]
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A page in a search result or the index.</summary>
public class KnowledgePageSummaryDto
{
    /// <summary>Slug (well-known name) of the page; cite pages by it.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Runtime id of the page.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>Title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Kind (Concept, Entity, HowTo, Summary, Skill, Schema).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Verification status (Draft, InReview, Verified, Rejected).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Summary sentence.</summary>
    public string? Summary { get; set; }

    /// <summary>Current version number.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>Relevance score of a search (higher first); 0 in the index.</summary>
    public double Score { get; set; }

    /// <summary>The MCP resource URI of the page (<c>knowledge-page://{tenantId}/{slug}</c>).</summary>
    public string Uri { get; set; } = string.Empty;
}

/// <summary>Base envelope of the knowledge tools.</summary>
public class KnowledgeResponse
{
    /// <summary>True on success.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when the call failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Machine-readable error code, e.g. <c>ContentRejected</c>, <c>VersionConflict</c>, <c>NotFound</c>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable outcome.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the call ran in.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Result of <c>knowledge_search</c>.</summary>
public class KnowledgeSearchResponse : KnowledgeResponse
{
    /// <summary>Matching pages, best first.</summary>
    public List<KnowledgePageSummaryDto> Pages { get; set; } = [];
}

/// <summary>Result of <c>knowledge_get</c>.</summary>
public class KnowledgePageResponse : KnowledgeResponse
{
    /// <summary>The page.</summary>
    public KnowledgePageSummaryDto? Page { get; set; }

    /// <summary>The version shown (the current one unless a version was asked for).</summary>
    public int Version { get; set; }

    /// <summary>The page (or the asked version) rendered as markdown.</summary>
    public string? Markdown { get; set; }

    /// <summary>Slugs of the pages this page links to.</summary>
    public List<string> LinksTo { get; set; } = [];

    /// <summary>RtIds of the source artifacts the page was generated from.</summary>
    public List<string> GeneratedFrom { get; set; } = [];

    /// <summary>The sections.</summary>
    public List<KnowledgeSectionDto> Sections { get; set; } = [];

    /// <summary>The citations.</summary>
    public List<KnowledgeCitationDto> Citations { get; set; } = [];

    /// <summary>Claims flagged by a verification pass.</summary>
    public List<KnowledgeHallucinationFlagDto> HallucinationFlags { get; set; } = [];
}

/// <summary>Result of the knowledge write tools.</summary>
public class KnowledgeWriteResponse : KnowledgeResponse
{
    /// <summary>The page after the write.</summary>
    public KnowledgePageSummaryDto? Page { get; set; }

    /// <summary>True when the page was created by this call.</summary>
    public bool Created { get; set; }

    /// <summary>True when <c>expectedVersion</c> was stale; nothing was written.</summary>
    public bool IsConflict { get; set; }

    /// <summary>Cited or named source artifacts that were linked with GeneratedFrom.</summary>
    public List<string> LinkedSources { get; set; } = [];

    /// <summary>Cited rtIds that are not SourceArtifacts of the tenant (kept as citations, not linked).</summary>
    public List<string> UnresolvedSources { get; set; } = [];
}
