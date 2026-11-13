using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.McpServices.Services.Knowledge;

/// <summary>The content of a knowledge page or of one of its versions.</summary>
internal sealed record KnowledgeContent(
    string Title,
    int Kind,
    string? Summary,
    IReadOnlyList<KnowledgeSectionDto> Sections,
    IReadOnlyList<KnowledgeCitationDto> Citations,
    IReadOnlyList<KnowledgeHallucinationFlagDto> HallucinationFlags,
    int Status);

/// <summary>
///     Run metadata of a write (prototype WikiEntry run metadata, stored on the version). AB#8657 adds the run data the
///     ingest pipeline passes: tokens (<c>PromptTokens</c>, <c>CompletionTokens</c>), cost (<c>CostUsd</c>), reviewer
///     rounds (<c>ReviewerIterations</c>) and the run reference (<c>RunRef</c>); AB#8719 the generation latency
///     (<c>LatencySeconds</c>). A <c>null</c> value is not written.
/// </summary>
internal sealed record KnowledgeRunMetadata(
    string? PromptName = null,
    int? PromptVersion = null,
    string? ModelId = null,
    string? RunRef = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    double? CostUsd = null,
    int? ReviewerRounds = null,
    double? LatencySeconds = null);

/// <summary>
///     The knowledge base model of System.Ai 4.7.0 (AB#8483) as the knowledge tools use it (AB#8484): ids, enum names,
///     reading and writing page content, versions, links and the markdown rendering. Every page write goes through
///     <see cref="BuildWrite" />: it appends a <c>KnowledgePageVersion</c> and points the page at it.
/// </summary>
internal static class KnowledgePages
{
    public const string PageType = "System.Ai/KnowledgePage";
    public const string VersionType = "System.Ai/KnowledgePageVersion";
    public const string SourceArtifactType = "System.Ai/SourceArtifact";
    public const string GeneratedFromRole = "System.Ai/GeneratedFrom";
    public const string LinksToRole = "System.Ai/LinksTo";
    public const string VersionOfRole = "System.Ai/VersionOf";
    public const string SectionRecord = "System.Ai/WikiSection";
    public const string CitationRecord = "System.Ai/Citation";
    public const string FlagRecord = "System.Ai/HallucinationFlag";

    /// <summary>
    ///     AB#8575: the trusted in-process write channel of the knowledge write tools. Must equal the <c>channels</c> entry
    ///     of the <c>writeAccess</c> block of <c>KnowledgePage</c> and <c>KnowledgePageVersion</c> in System.Ai 4.8.0
    ///     (octo-ai-services <c>src/SystemAiCkModel/ConstructionKit/types/knowledgePage*.yaml</c>); a mismatch makes every
    ///     knowledge write of a client-credentials caller fail with message 144.
    /// </summary>
    public const string WriteChannel = "System.Ai/KnowledgeTools";

    public static readonly string[] KindNames = ["Concept", "Entity", "HowTo", "Summary", "Skill", "Schema"];
    public static readonly string[] OriginNames = ["Ingest", "Query", "Manual", "Blueprint"];
    public static readonly string[] StatusNames = ["Draft", "InReview", "Verified", "Rejected"];

    public const int StatusDraft = 0;
    public const int StatusVerified = 2;
    public const int StatusRejected = 3;

    public static RtCkId<CkTypeId> PageTypeId => new(PageType);
    public static RtCkId<CkTypeId> VersionTypeId => new(VersionType);

    /// <summary>Resource URI of a page.</summary>
    public static string Uri(string tenantId, string slug) => $"knowledge-page://{tenantId}/{slug}";

    /// <summary>Well-known name of a version entity.</summary>
    public static string VersionName(string slug, int version) => $"{slug}@{version}";

    /// <summary>Lower-case words joined by hyphens: letters, digits, '-', '.', '_'; 2-120 characters.</summary>
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length is >= 2 and <= 120 &&
        slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_') &&
        char.IsAsciiLetterOrDigit(slug[0]);

    /// <summary>Parses an enum name (case-insensitive) or a number.</summary>
    public static int? ParseEnum(string? value, string[] names)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var index = Array.FindIndex(names, n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            return index;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
               number >= 0 && number < names.Length
            ? number
            : null;
    }

    public static string Name(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    public static async Task<RtEntity?> FindBySlugAsync(ITenantRepository repository, IOctoSession session,
        string ckTypeId, string wellKnownName)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldEquals("rtWellKnownName", wellKnownName);
        var result = await repository.GetRtEntitiesByTypeAsync(session, new RtCkId<CkTypeId>(ckTypeId), options, 0, 2);
        return result.Items.FirstOrDefault(e => e.RtWellKnownName == wellKnownName);
    }

    public static async Task<IReadOnlyList<RtEntity>> ListPagesAsync(ITenantRepository repository,
        IOctoSession session, string? textSearch, int take)
    {
        var options = RtEntityQueryOptions.Create();
        if (!string.IsNullOrWhiteSpace(textSearch))
        {
            options.TextSearch(textSearch);
        }

        var result = await repository.GetRtEntitiesByTypeAsync(session, PageTypeId, options, 0, take);
        return result.Items.Where(p => !string.IsNullOrEmpty(p.RtWellKnownName)).ToList();
    }

    public static KnowledgeContent ReadContent(RtEntity entity) => new(
        entity.GetAttributeStringValueOrDefault("Title") ?? string.Empty,
        ToInt(entity.GetAttributeValueOrDefault("Kind")),
        entity.GetAttributeStringValueOrDefault("Summary"),
        Records(entity, "Sections").Select(r => new KnowledgeSectionDto
        {
            Heading = Str(r, "Heading"),
            SectionContent = Str(r, "SectionContent"),
            SortOrder = ToInt(Val(r, "SortOrder"))
        }).OrderBy(s => s.SortOrder).ToList(),
        Records(entity, "Citations").Select(r => new KnowledgeCitationDto
        {
            SectionRef = Str(r, "SectionRef"),
            SourceQuote = Str(r, "SourceQuote"),
            SourceAnchor = NullIfEmpty(Str(r, "SourceAnchor")),
            SourceRtId = NullIfEmpty(Str(r, "SourceRtId"))
        }).ToList(),
        Records(entity, "HallucinationFlags").Select(r => new KnowledgeHallucinationFlagDto
        {
            Claim = Str(r, "Claim"),
            Reason = Str(r, "Reason")
        }).ToList(),
        ToInt(entity.GetAttributeValueOrDefault("VerificationStatus")));

    public static int CurrentVersion(RtEntity page) => ToInt(page.GetAttributeValueOrDefault("CurrentVersion"));

    public static KnowledgePageSummaryDto Summarize(string tenantId, RtEntity page, double score = 0)
    {
        var content = ReadContent(page);
        var slug = page.RtWellKnownName ?? string.Empty;
        return new KnowledgePageSummaryDto
        {
            Slug = slug,
            RtId = page.RtId.ToString(),
            Title = content.Title,
            Kind = Name(KindNames, content.Kind),
            Status = Name(StatusNames, content.Status),
            Summary = content.Summary,
            CurrentVersion = CurrentVersion(page),
            Score = score,
            Uri = Uri(tenantId, slug)
        };
    }

    /// <summary>The fields of a content the content guard checks.</summary>
    public static IReadOnlyCollection<ContentGuardField> GuardFields(KnowledgeContent content, string slug)
    {
        var fields = new List<ContentGuardField>
        {
            new("slug", slug), new("title", content.Title), new("summary", content.Summary)
        };
        for (var i = 0; i < content.Sections.Count; i++)
        {
            fields.Add(new ContentGuardField($"sections[{i}]",
                content.Sections[i].Heading + "\n" + content.Sections[i].SectionContent));
        }

        for (var i = 0; i < content.Citations.Count; i++)
        {
            fields.Add(new ContentGuardField($"citations[{i}]",
                string.Join("\n", content.Citations[i].SectionRef, content.Citations[i].SourceQuote,
                    content.Citations[i].SourceAnchor, content.Citations[i].SourceRtId)));
        }

        for (var i = 0; i < content.HallucinationFlags.Count; i++)
        {
            fields.Add(new ContentGuardField($"hallucinationFlags[{i}]",
                content.HallucinationFlags[i].Claim + "\n" + content.HallucinationFlags[i].Reason));
        }

        return fields;
    }

    /// <summary>Line break of <see cref="Render" />; never <c>Environment.NewLine</c>, the content hash is the same on every OS.</summary>
    private const char Nl = '\n';

    /// <summary>
    ///     Renders a content as markdown (title, summary, sections in order, sources). Lines end with <c>\n</c> on every
    ///     platform: the output is the input of the content hash, which octo-cli recomputes (AB#8565).
    /// </summary>
    public static string Render(KnowledgeContent content, string slug, int version)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(content.Title).Append(Nl).Append(Nl);
        sb.Append("> slug `").Append(slug).Append("` · ").Append(Name(KindNames, content.Kind)).Append(" · ")
            .Append(Name(StatusNames, content.Status)).Append(" · version ")
            .Append(version.ToString(CultureInfo.InvariantCulture)).Append(Nl).Append(Nl);
        if (!string.IsNullOrWhiteSpace(content.Summary))
        {
            sb.Append(content.Summary).Append(Nl).Append(Nl);
        }

        foreach (var section in content.Sections.OrderBy(s => s.SortOrder))
        {
            sb.Append("## ").Append(section.Heading).Append(Nl).Append(Nl)
                .Append(section.SectionContent).Append(Nl).Append(Nl);
        }

        if (content.Citations.Count > 0)
        {
            sb.Append("## Citations").Append(Nl).Append(Nl);
            foreach (var c in content.Citations)
            {
                sb.Append("- [").Append(c.SectionRef).Append("] \"").Append(c.SourceQuote).Append('"');
                if (!string.IsNullOrWhiteSpace(c.SourceRtId))
                {
                    sb.Append(" (source ").Append(c.SourceRtId);
                    if (!string.IsNullOrWhiteSpace(c.SourceAnchor))
                    {
                        sb.Append(", ").Append(c.SourceAnchor);
                    }

                    sb.Append(')');
                }

                sb.Append(Nl);
            }

            sb.Append(Nl);
        }

        if (content.HallucinationFlags.Count > 0)
        {
            sb.Append("## Unsupported claims").Append(Nl).Append(Nl);
            foreach (var f in content.HallucinationFlags)
            {
                sb.Append("- ").Append(f.Claim).Append(" (").Append(f.Reason).Append(')').Append(Nl);
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    ///     Builds the entity updates of one page write: the page (insert or update) and a new
    ///     <c>KnowledgePageVersion</c> with the full content, author, time and run metadata.
    /// </summary>
    /// <param name="existing">The stored page; <c>null</c> creates it.</param>
    /// <param name="slug">Slug of the page.</param>
    /// <param name="content">The new content.</param>
    /// <param name="origin">Origin of a new page (ignored for an existing one).</param>
    /// <param name="author">Author of the version.</param>
    /// <param name="changeNote">Why the version was written.</param>
    /// <param name="restoredFrom">The restored version, for a restore.</param>
    /// <param name="run">Run metadata.</param>
    /// <param name="now">Time of the write.</param>
    /// <param name="highestVersion">
    ///     The highest version number the page already has (<see cref="KnowledgePlatformUpdates.HighestVersionAsync" />).
    ///     AB#8780: a blueprint update brings versions above the current one (<c>&lt;slug&gt;@&lt;n&gt;</c>), and the
    ///     versions of a deleted page stay; the new version takes the next free number, so a version name is never used
    ///     twice.
    /// </param>
    /// <param name="recordBase">
    ///     True when the tenant's model has <c>KnowledgePageVersion.BaseVersion</c> (System.Ai 4.11.0): the new version
    ///     records the page's current version as the one it was written on, so the platform-update rule can tell which
    ///     Blueprint versions the edit already saw (AB#8780, ancestor chain).
    /// </param>
    public static (RtEntity Page, RtEntity Version, List<IEntityUpdateInfo<RtEntity>> Updates, int VersionNumber)
        BuildWrite(RtEntity? existing, string slug, KnowledgeContent content, int origin, string author,
            string changeNote, int? restoredFrom, KnowledgeRunMetadata? run, DateTime now, int highestVersion = 0,
            bool recordBase = false)
    {
        var versionNumber = Math.Max(existing == null ? 0 : CurrentVersion(existing), highestVersion) + 1;
        var hash = Hash(Render(content, slug, 0));

        var page = new RtEntity
        {
            RtId = existing?.RtId ?? OctoObjectId.GenerateNewId(),
            CkTypeId = PageTypeId,
            RtWellKnownName = slug
        };
        SetContent(page, content);
        page.SetAttributeValue("ContentHash", AttributeValueTypesDto.String, hash);
        page.SetAttributeValue("CompiledAt", AttributeValueTypesDto.DateTime, now);
        page.SetAttributeValue("CurrentVersion", AttributeValueTypesDto.Int, versionNumber);
        if (existing == null)
        {
            page.SetAttributeValue("Origin", AttributeValueTypesDto.Enum, origin);
        }

        var version = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = VersionTypeId,
            RtWellKnownName = VersionName(slug, versionNumber)
        };
        SetContent(version, content);
        version.SetAttributeValue("VersionNumber", AttributeValueTypesDto.Int, versionNumber);
        version.SetAttributeValue("ContentHash", AttributeValueTypesDto.String, hash);
        version.SetAttributeValue("Author", AttributeValueTypesDto.String, author);
        version.SetAttributeValue("AuthoredAt", AttributeValueTypesDto.DateTime, now);
        version.SetAttributeValue("ChangeNote", AttributeValueTypesDto.String, changeNote);
        if (recordBase && existing != null)
        {
            version.SetAttributeValue(KnowledgePlatformUpdates.BaseVersionAttribute, AttributeValueTypesDto.Int,
                CurrentVersion(existing));
        }

        if (restoredFrom != null)
        {
            version.SetAttributeValue("RestoredFromVersion", AttributeValueTypesDto.Int, restoredFrom.Value);
        }

        if (run?.PromptName != null)
        {
            version.SetAttributeValue("PromptName", AttributeValueTypesDto.String, run.PromptName);
        }

        if (run?.PromptVersion != null)
        {
            version.SetAttributeValue("PromptVersion", AttributeValueTypesDto.Int, run.PromptVersion.Value);
        }

        if (run?.ModelId != null)
        {
            version.SetAttributeValue("ModelId", AttributeValueTypesDto.String, run.ModelId);
        }

        if (run?.RunRef != null)
        {
            version.SetAttributeValue("RunRef", AttributeValueTypesDto.String, run.RunRef);
        }

        if (run?.InputTokens != null)
        {
            version.SetAttributeValue("PromptTokens", AttributeValueTypesDto.Int, run.InputTokens.Value);
        }

        if (run?.OutputTokens != null)
        {
            version.SetAttributeValue("CompletionTokens", AttributeValueTypesDto.Int, run.OutputTokens.Value);
        }

        if (run?.CostUsd != null)
        {
            version.SetAttributeValue("CostUsd", AttributeValueTypesDto.Double, run.CostUsd.Value);
        }

        if (run?.ReviewerRounds != null)
        {
            version.SetAttributeValue("ReviewerIterations", AttributeValueTypesDto.Int, run.ReviewerRounds.Value);
        }

        if (run?.LatencySeconds != null)
        {
            version.SetAttributeValue("LatencySeconds", AttributeValueTypesDto.Double, run.LatencySeconds.Value);
        }

        var pageId = new RtEntityId(PageTypeId, page.RtId);
        var updates = new List<IEntityUpdateInfo<RtEntity>>
        {
            existing == null
                ? EntityUpdateInfo<RtEntity>.CreateInsert(PageTypeId, page)
                : EntityUpdateInfo<RtEntity>.CreateUpdate(pageId, page),
            EntityUpdateInfo<RtEntity>.CreateInsert(VersionTypeId, version)
        };
        return (page, version, updates, versionNumber);
    }

    public static AssociationUpdateInfo VersionOf(RtEntity version, RtEntity page) =>
        AssociationUpdateInfo.CreateInsert(new RtEntityId(VersionTypeId, version.RtId),
            new RtEntityId(PageTypeId, page.RtId), new RtCkId<CkAssociationRoleId>(VersionOfRole));

    /// <summary>The outbound targets of a page for a role.</summary>
    public static async Task<IReadOnlyList<OctoObjectId>> OutboundTargetsAsync(ITenantRepository repository,
        IOctoSession session, OctoObjectId pageRtId, string roleId)
    {
        var associations = await repository.GetRtAssociationsAsync(session, new RtEntityId(PageTypeId, pageRtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, new RtCkId<CkAssociationRoleId>(roleId)));
        return associations.Items.Select(a => a.TargetRtId).ToList();
    }

    /// <summary>Sets the content attributes (title, kind, summary, status, sections, citations, flags) of a page or version.</summary>
    public static void SetContent(RtEntity entity, KnowledgeContent content)
    {
        entity.SetAttributeValue("Title", AttributeValueTypesDto.String, content.Title);
        entity.SetAttributeValue("Kind", AttributeValueTypesDto.Enum, content.Kind);
        entity.SetAttributeValue("Summary", AttributeValueTypesDto.String, content.Summary ?? string.Empty);
        entity.SetAttributeValue("VerificationStatus", AttributeValueTypesDto.Enum, content.Status);
        entity.SetAttributeValue("Sections", AttributeValueTypesDto.RecordArray, content.Sections
            .Select(s => Record(SectionRecord, ("Heading", s.Heading), ("SectionContent", s.SectionContent),
                ("SortOrder", s.SortOrder))).ToList());
        entity.SetAttributeValue("Citations", AttributeValueTypesDto.RecordArray, content.Citations
            .Select(c => Record(CitationRecord, ("SectionRef", c.SectionRef), ("SourceQuote", c.SourceQuote),
                ("SourceAnchor", c.SourceAnchor), ("SourceRtId", c.SourceRtId))).ToList());
        entity.SetAttributeValue("HallucinationFlags", AttributeValueTypesDto.RecordArray, content.HallucinationFlags
            .Select(f => Record(FlagRecord, ("Claim", f.Claim), ("Reason", f.Reason))).ToList());
    }

    private static RtRecord Record(string recordId, params (string Name, object? Value)[] attributes) =>
        new(new RtCkId<CkRecordId>(recordId),
            attributes.Where(a => a.Value != null).ToDictionary(a => a.Name, a => a.Value));

    private static IEnumerable<RtRecord> Records(RtEntity entity, string attributeName) =>
        entity.GetAttributeValueOrDefault(attributeName) is IEnumerable<RtRecord> records
            ? records
            : (IEnumerable<RtRecord>?)entity.GetRtRecordAttributeValuesOrDefault<RtRecord>(attributeName) ?? [];

    private static object? Val(RtRecord record, string name) =>
        record.Attributes.TryGetValue(name, out var value) ? value : null;

    private static string Str(RtRecord record, string name) => Val(record, name)?.ToString() ?? string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static int ToInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
        _ => int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0
    };

    /// <summary>SHA-256 (lower-case hex) of a text; the content hash is taken over <see cref="Render" /> with version 0.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
