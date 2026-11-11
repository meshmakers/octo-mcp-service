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
    [Description("Category: Contradiction, Orphan, Stale, MissingLink, UnsupportedClaim or CitationNotInSource.")]
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
