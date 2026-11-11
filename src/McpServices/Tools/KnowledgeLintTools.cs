using System.ComponentModel;
using System.Globalization;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AB#8488: the write channel of the nightly knowledge lint. One tool, one fixed type (System.Ai
///     <c>KnowledgeLintReport</c>): the lint pipeline (service account <c>knowledge-linter</c>, <c>McpToolCall@1</c>)
///     hands its findings over and the tool writes one report per call. The finding texts run through the content guard
///     (a finding can quote a source artifact), the report is always written as unvalidated, and the tool changes no
///     knowledge page (no status, never <c>Verified</c>). The generic entity tools refuse the type
///     (<see cref="KnowledgeWriteRefusal" />).
/// </summary>
[McpServerToolType]
public static class KnowledgeLintTools
{
    /// <summary>Runtime id of the report type.</summary>
    public const string ReportType = "System.Ai/KnowledgeLintReport";

    /// <summary>Runtime id of the finding record.</summary>
    public const string FindingRecord = "System.Ai/KnowledgeLintFinding";

    /// <summary>Maximum number of findings of one report.</summary>
    public const int MaxFindings = 500;

    /// <summary>Maximum length of a finding detail.</summary>
    public const int MaxDetailLength = 2000;

    /// <summary>Maximum length of the report name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The attribute that only the System.Ai version of AB#8488 has on the report type.</summary>
    internal const string ValidatedAttribute = "IsValidated";

    /// <summary>Names of the System.Ai enum <c>KnowledgeLintFindingKind</c>, by key.</summary>
    public static readonly string[] FindingKindNames =
        ["Contradiction", "Orphan", "Stale", "MissingLink", "UnsupportedClaim", "CitationNotInSource"];

    /// <summary>Writes one knowledge lint report.</summary>
    [McpServerTool(Name = "knowledge_write_lint_report")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Write the report of one knowledge lint run (KnowledgeLintReport, readable in Studio's generic entity view). Takes " +
        "the findings of the run: kind (Contradiction, Orphan, Stale, MissingLink, UnsupportedClaim, CitationNotInSource), " +
        "pageRef (page slug), optional relatedPageRef (second page slug) and detail (one or two sentences, up to 2000 " +
        "characters; at most 500 findings). Every call writes a new report; nothing is updated and no knowledge page is " +
        "changed. The report is always written unvalidated (IsValidated false): the precision of the lint is not measured " +
        "yet, so nothing may act on it automatically. Text that looks like a credential or a secret value of the tenant " +
        "is refused with ContentRejected and nothing is written. Run data (all optional): inputTokens, outputTokens, " +
        "cost (USD), verifiedThrough (the compile time up to which pages were checked with the LLM, the cursor of the " +
        "next run) and runRef; a negative number is refused.")]
    public static async Task<KnowledgeLintReportResponse> KnowledgeWriteLintReport(
        McpServer server,
        [Description("When the lint run started (UTC).")] DateTime startedAt,
        [Description("Number of pages the run checked, 0 or more.")] int pagesChecked,
        [Description("The findings: kind, pageRef, relatedPageRef, detail. Omitted or []: a clean run.")]
        List<KnowledgeLintFindingDto>? findings = null,
        [Description("When the lint run ended (UTC); not before startedAt.")] DateTime? completedAt = null,
        [Description("Name of the report; default 'knowledge-lint <startedAt>'.")] string? name = null,
        [Description("Input tokens of the LLM calls of the run, 0 or more.")] int? inputTokens = null,
        [Description("Output tokens of the LLM calls of the run, 0 or more.")] int? outputTokens = null,
        [Description("Estimated cost of the run in USD, 0 or more.")] double? cost = null,
        [Description("Compile time (UTC) up to which pages were checked with the LLM; the next run continues after it.")]
        DateTime? verifiedThrough = null,
        [Description("Reference of the run: the pipeline execution id (up to 200 characters).")] string? runRef = null,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var invalid = Validate(startedAt, pagesChecked, findings ?? [], completedAt, name, inputTokens, outputTokens,
            cost, runRef, out var kinds, verifiedThrough, DateTime.UtcNow);
        if (invalid != null)
        {
            return Invalid(invalid);
        }

        var ctx = await KnowledgeTools.OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeLintReportResponse { ErrorMessage = ctx.Error };
        }

        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        if (!ckCache.TryGetRtCkType(ctx.TenantId!, new RtCkId<CkTypeId>(ReportType), out var reportType) ||
            reportType == null ||
            !reportType.AllAttributes.Values.Any(a => a.AttributeName == ValidatedAttribute))
        {
            return new KnowledgeLintReportResponse
            {
                TenantId = ctx.TenantId,
                ErrorMessage = $"Tenant '{ctx.TenantId}' cannot store lint reports: it needs the System.Ai version whose " +
                               $"KnowledgeLintReport has {ValidatedAttribute} (AB#8488)."
            };
        }

        var reportName = string.IsNullOrWhiteSpace(name)
            ? "knowledge-lint " + startedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture)
            : name.Trim();
        var list = findings ?? [];

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var refusal = await GuardAsync(server, ctx, session, ckCache, reportName, list, runRef, cancellationToken);
            if (refusal != null)
            {
                return refusal;
            }

            var report = BuildReport(reportName, startedAt, completedAt, pagesChecked, list, kinds, inputTokens,
                outputTokens, cost, verifiedThrough, runRef);
            session.StartTransaction();
            var result = new OperationResult();
            try
            {
                await ctx.Repo.ApplyChangesAsync(session,
                    [EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>(ReportType), report)], [], result);
                if (result.HasErrors || result.HasFatalErrors)
                {
                    await session.AbortTransactionAsync();
                    return new KnowledgeLintReportResponse
                    {
                        TenantId = ctx.TenantId, ErrorMessage = $"The write failed: {result.GetMessages()}"
                    };
                }

                await session.CommitTransactionAsync();
            }
            catch
            {
                await TryAbortAsync(session);
                throw;
            }

            var byKind = kinds.GroupBy(k => FindingKindNames[k])
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return new KnowledgeLintReportResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = report.RtId.ToString(),
                Name = reportName,
                FindingCount = list.Count,
                FindingsByKind = byKind,
                IsValidated = false,
                Message = $"Wrote lint report '{reportName}' with {list.Count} finding(s) over {pagesChecked} page(s) " +
                          "(unvalidated)."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeLintReportResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Checks the arguments; returns the error message or <c>null</c>. <paramref name="kinds" /> are the enum keys.</summary>
    internal static string? Validate(DateTime startedAt, int pagesChecked, IReadOnlyList<KnowledgeLintFindingDto> findings,
        DateTime? completedAt, string? name, int? inputTokens, int? outputTokens, double? cost, string? runRef,
        out List<int> kinds, DateTime? verifiedThrough = null, DateTime? now = null)
    {
        kinds = [];
        if (startedAt == default)
        {
            return "startedAt is required.";
        }

        if (pagesChecked < 0)
        {
            return $"pagesChecked must not be negative ({pagesChecked}).";
        }

        if (completedAt != null && completedAt.Value.ToUniversalTime() < startedAt.ToUniversalTime())
        {
            return "completedAt must not be before startedAt.";
        }

        // The cursor of the next run: a value after the end of this run (or in the future) would make later runs skip
        // every page compiled before it.
        var horizon = (now ?? DateTime.UtcNow).AddMinutes(5);
        if (completedAt != null && completedAt.Value.ToUniversalTime() > horizon)
        {
            return "completedAt must not be in the future.";
        }

        var latest = completedAt?.ToUniversalTime() ?? horizon;
        if (verifiedThrough != null && verifiedThrough.Value.ToUniversalTime() > latest)
        {
            return completedAt != null
                ? "verifiedThrough must not be after completedAt."
                : "verifiedThrough must not be in the future.";
        }

        if (name != null && name.Trim().Length > MaxNameLength)
        {
            return $"name must not exceed {MaxNameLength} characters.";
        }

        var runDataError = KnowledgeTools.ValidateRunData(inputTokens, outputTokens, cost, null, runRef);
        if (runDataError != null)
        {
            return runDataError;
        }

        if (findings.Count > MaxFindings)
        {
            return $"A report holds at most {MaxFindings} findings ({findings.Count} given); report the first " +
                   $"{MaxFindings} and say so in a finding detail.";
        }

        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            if (f == null)
            {
                return $"findings[{i}] is empty.";
            }

            var kind = KnowledgePages.ParseEnum(f.Kind, FindingKindNames);
            if (kind == null)
            {
                return $"findings[{i}].kind '{f.Kind}' is unknown: use {string.Join(", ", FindingKindNames)}.";
            }

            if (!KnowledgePages.IsValidSlug(f.PageRef))
            {
                return $"findings[{i}].pageRef '{f.PageRef}' is not a page slug.";
            }

            if (f.RelatedPageRef != null && !KnowledgePages.IsValidSlug(f.RelatedPageRef))
            {
                return $"findings[{i}].relatedPageRef '{f.RelatedPageRef}' is not a page slug.";
            }

            if (string.IsNullOrWhiteSpace(f.Detail))
            {
                return $"findings[{i}].detail is required.";
            }

            if (f.Detail.Length > MaxDetailLength)
            {
                return $"findings[{i}].detail has {f.Detail.Length} characters, more than {MaxDetailLength}.";
            }

            kinds.Add(kind.Value);
        }

        return null;
    }

    private static RtEntity BuildReport(string name, DateTime startedAt, DateTime? completedAt, int pagesChecked,
        IReadOnlyList<KnowledgeLintFindingDto> findings, IReadOnlyList<int> kinds, int? inputTokens, int? outputTokens,
        double? cost, DateTime? verifiedThrough, string? runRef)
    {
        var report = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>(ReportType)
        };
        report.SetAttributeValue("Name", AttributeValueTypesDto.String, name);
        report.SetAttributeValue("StartedAt", AttributeValueTypesDto.DateTime, startedAt.ToUniversalTime());
        if (completedAt != null)
        {
            report.SetAttributeValue("CompletedAt", AttributeValueTypesDto.DateTime, completedAt.Value.ToUniversalTime());
        }

        report.SetAttributeValue("PagesChecked", AttributeValueTypesDto.Int, pagesChecked);
        // Never validated over MCP: the precision of the lint is measured on the Control Set first (AB#8488 AC2), and a
        // person sets the flag outside MCP once it is.
        report.SetAttributeValue(ValidatedAttribute, AttributeValueTypesDto.Boolean, false);
        report.SetAttributeValue("Findings", AttributeValueTypesDto.RecordArray, findings
            .Select((f, i) => new RtRecord(new RtCkId<CkRecordId>(FindingRecord),
                new Dictionary<string, object?>
                    {
                        ["Kind"] = kinds[i],
                        ["PageRef"] = f.PageRef.Trim(),
                        ["RelatedPageRef"] = f.RelatedPageRef?.Trim(),
                        ["Detail"] = f.Detail.Trim()
                    }
                    .Where(a => a.Value != null).ToDictionary(a => a.Key, a => a.Value)))
            .ToList());
        if (inputTokens != null)
        {
            report.SetAttributeValue("InputTokens", AttributeValueTypesDto.Int, inputTokens.Value);
        }

        if (outputTokens != null)
        {
            report.SetAttributeValue("OutputTokens", AttributeValueTypesDto.Int, outputTokens.Value);
        }

        if (cost != null)
        {
            report.SetAttributeValue("Cost", AttributeValueTypesDto.Double, cost.Value);
        }

        if (verifiedThrough != null)
        {
            report.SetAttributeValue("VerifiedThrough", AttributeValueTypesDto.DateTime,
                verifiedThrough.Value.ToUniversalTime());
        }

        if (runRef != null)
        {
            report.SetAttributeValue("RunRef", AttributeValueTypesDto.String, runRef);
        }

        return report;
    }

    /// <summary>
    ///     The content guard over every text of the report (name, run reference, each finding's refs and detail). Fails
    ///     closed without a guard, like the page tools.
    /// </summary>
    private static async Task<KnowledgeLintReportResponse?> GuardAsync(McpServer server,
        KnowledgeTools.KnowledgeContext ctx, IOctoSession session, ICkCacheService ckCache, string name,
        IReadOnlyList<KnowledgeLintFindingDto> findings, string? runRef, CancellationToken ct)
    {
        var guard = server.Services!.GetService<IContentGuard>();
        if (guard == null)
        {
            return new KnowledgeLintReportResponse
            {
                TenantId = ctx.TenantId,
                ErrorCode = ContentGuardResult.ErrorCode,
                ErrorMessage = "The content guard is not configured; knowledge writes are refused."
            };
        }

        var protector = server.Services!.GetService<ISecretAttributeProtector>();
        var context = new ContentGuardContext(ctx.TenantId!, ctx.Author!, "knowledge_write_lint_report",
            token => TenantSecretFingerprints.ReadAsync(ctx.Repo, session, ckCache, protector, token));
        var fields = new List<ContentGuardField> { new("name", name), new("runRef", runRef) };
        for (var i = 0; i < findings.Count; i++)
        {
            fields.Add(new ContentGuardField($"findings[{i}].pageRef", findings[i].PageRef));
            fields.Add(new ContentGuardField($"findings[{i}].relatedPageRef", findings[i].RelatedPageRef));
            fields.Add(new ContentGuardField($"findings[{i}].detail", findings[i].Detail));
        }

        var result = await guard.CheckAsync(context, fields.Where(f => f.Text != null).ToList(), ct);
        return result.IsRejected
            ? new KnowledgeLintReportResponse
            {
                TenantId = ctx.TenantId,
                ErrorCode = ContentGuardResult.ErrorCode,
                ErrorMessage = result.Message
            }
            : null;
    }

    private static async Task TryAbortAsync(IOctoSession session)
    {
        try
        {
            await session.AbortTransactionAsync();
        }
        catch (Exception)
        {
            // Already committed or aborted; the caller reports the original failure.
        }
    }

    private static KnowledgeLintReportResponse Invalid(string message) =>
        new() { ErrorCode = "InvalidArgument", ErrorMessage = message };
}
