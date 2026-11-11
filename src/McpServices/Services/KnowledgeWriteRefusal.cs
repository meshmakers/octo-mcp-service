using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8568: the generic entity tools never write the knowledge types of System.Ai (<c>KnowledgePage</c>,
///     <c>KnowledgeLintReport</c> (AB#8488, only through <c>knowledge_write_lint_report</c>),
///     <c>KnowledgePageVersion</c>, and types derived from them). Pages are written only through the knowledge tools
///     (AB#8484), which run the content guard and append a version with author and time; a generic create, update,
///     secret write, delete or runtime-model import would bypass both. Same shape as <see cref="GenericCreateRefusal" />:
///     a machine-readable code <see cref="Code" />, parameters <see cref="CkTypeIdParameter" /> and
///     <see cref="ToolParameter" />, a message with ids only (never entity data).
///     <para>
///         Delete is refused as well (decision of the CPO, 2026-11-08): deleting a knowledge page is not a knowledge
///         operation in M1. Agents set a page to <c>Rejected</c> with <c>knowledge_set_status</c>; a person deletes pages
///         outside MCP (GraphQL or octo-cli).
///     </para>
/// </summary>
internal static class KnowledgeWriteRefusal
{
    /// <summary>Error code of the refusal.</summary>
    public const string Code = "GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL";

    /// <summary>Parameter with the CK type the caller tried to write.</summary>
    public const string CkTypeIdParameter = "ckTypeId";

    /// <summary>Parameter with the knowledge tool(s) to use instead (comma-separated), or <c>none</c> for a delete.</summary>
    public const string ToolParameter = "tool";

    /// <summary>The runtime ids of the protected types.</summary>
    public static readonly IReadOnlyList<RtCkId<CkTypeId>> ProtectedTypes =
    [
        new("System.Ai/KnowledgePage"),
        new("System.Ai/KnowledgePageVersion"),
        new(LintReportType)
    ];

    /// <summary>AB#8488: the lint report is written only by <c>knowledge_write_lint_report</c> (content guard, unvalidated).</summary>
    private const string LintReportType = "System.Ai/KnowledgeLintReport";

    private const string LintReportTool = "knowledge_write_lint_report";

    /// <summary>The kind of generic write.</summary>
    public enum Operation
    {
        /// <summary>create_entity, create_entity_with_secrets.</summary>
        Create,

        /// <summary>update_entity, set_entity_secrets.</summary>
        Update,

        /// <summary>delete_entity.</summary>
        Delete,

        /// <summary>import_runtime_model.</summary>
        Import
    }

    private const string WriteTools = "knowledge_upsert_page,knowledge_set_status,knowledge_restore_version,knowledge_link";

    /// <summary>
    ///     The refusal for a generic write on <paramref name="ckTypeId" /> (runtime or full id), or <c>null</c> when the
    ///     type is not a knowledge type. A type the cache does not know is compared by its id only.
    /// </summary>
    public static (string Message, IReadOnlyDictionary<string, string> Parameters)? TryGetRefusal(
        ICkCacheService ckCache, string tenantId, string? ckTypeId, Operation operation)
    {
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return null;
        }

        var runtimeId = AttributeAccessGuard.ToRuntimeId(ckTypeId);
        if (runtimeId == null || !IsKnowledgeType(ckCache, tenantId, runtimeId))
        {
            return null;
        }

        return Build(runtimeId.ToString(), operation, LintKindOf(ckCache, tenantId, runtimeId));
    }

    /// <summary>Whether a type is the lint report, a type the CK cache knows that derives from it, or neither (AB#8488).</summary>
    private enum LintKind
    {
        None,
        Report,
        Derived
    }

    private static LintKind LintKindOf(ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> runtimeId)
    {
        var lintReport = new RtCkId<CkTypeId>(LintReportType);
        if (runtimeId.Equals(lintReport))
        {
            return LintKind.Report;
        }

        return ckCache.TryGetRtCkType(tenantId, runtimeId, out var graph) && graph != null &&
               graph.GetBaseTypes(true).Any(t => t.ToRtCkId().Equals(lintReport))
            ? LintKind.Derived
            : LintKind.None;
    }

    /// <summary>The refusal message of an import whose file references the knowledge type or role <paramref name="id" />.</summary>
    public static string ImportMessage(string id) =>
        Build(id, Operation.Import,
            string.Equals(id, LintReportType, StringComparison.OrdinalIgnoreCase) ? LintKind.Report : LintKind.None).Message;

    /// <summary>The refusal message of an import whose file references the knowledge type <paramref name="runtimeId" /> the CK cache knows.</summary>
    public static string ImportMessage(ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> runtimeId) =>
        Build(runtimeId.ToString(), Operation.Import, LintKindOf(ckCache, tenantId, runtimeId)).Message;

    /// <summary>The refusal message of an import that references a CK type the tenant does not know (fail closed).</summary>
    public static string UnknownTypeImportMessage(string ckTypeId) =>
        $"The runtime model references the CK type '{Truncate(ckTypeId)}', which this tenant does not know. In a tenant " +
        "with knowledge types an unknown type cannot be ruled out as a knowledge page, so the file is not imported over " +
        "MCP. Nothing was imported.";

    /// <summary>
    ///     The refusal message of an import whose file could not be read or checked, or whose file type the check does
    ///     not read (fail closed). Deliberately generic: no exception text, which can carry server-side paths.
    /// </summary>
    public const string ImportCheckFailedMessage =
        "The uploaded runtime model could not be read to check it for knowledge types, so it is refused. Upload a valid " +
        "runtime model (.yaml, .json, or a .zip with one of them). Nothing was imported.";

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

    /// <summary>A protected type, or a type the CK cache knows that derives from one.</summary>
    internal static bool IsKnowledgeType(ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> runtimeId)
    {
        if (ProtectedTypes.Contains(runtimeId))
        {
            return true;
        }

        // Derived types inherit the protection: the knowledge tools are the only writers of anything that is a page.
        return ckCache.TryGetRtCkType(tenantId, runtimeId, out var graph) && graph != null &&
               graph.GetBaseTypes(true).Any(t => ProtectedTypes.Contains(t.ToRtCkId()));
    }

    private static (string Message, IReadOnlyDictionary<string, string> Parameters) Build(string typeId,
        Operation operation, LintKind lintKind)
    {
        var (message, tool) = lintKind switch
        {
            LintKind.Report => LintReportRefusal(typeId, operation),
            LintKind.Derived => DerivedLintReportRefusal(typeId, operation),
            _ => PageRefusal(typeId, operation)
        };

        return (message, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CkTypeIdParameter] = typeId,
            [ToolParameter] = tool
        });
    }

    private static (string Message, string Tool) LintReportRefusal(string typeId, Operation operation) => operation switch
    {
        Operation.Delete => (
            $"'{typeId}' is not deleted over MCP: lint reports are the history of the lint runs. A person deletes old " +
            "reports outside MCP (GraphQL or octo-cli).", "none"),
        Operation.Import => (
            $"The runtime model references '{typeId}'. Lint reports are not imported generically (no content guard); " +
            $"write them with {LintReportTool}. Nothing was imported.", LintReportTool),
        _ => (
            $"'{typeId}' is written only through {LintReportTool}, which runs the content guard and writes the report " +
            "unvalidated. Nothing was written.", LintReportTool)
    };

    private static (string Message, string Tool) DerivedLintReportRefusal(string typeId, Operation operation) => (
        $"'{typeId}' derives from {LintReportType}. Derived lint reports are not written or imported over MCP: " +
        $"{LintReportTool} writes {LintReportType} only. " +
        (operation == Operation.Import ? "Nothing was imported." : "Nothing was written."), "none");

    private static (string Message, string Tool) PageRefusal(string typeId, Operation operation)
    {
        return operation switch
        {
            Operation.Delete => (
                $"'{typeId}' is not deleted over MCP: knowledge pages and their versions keep their history. Set the " +
                "page to Rejected with knowledge_set_status; a person deletes pages outside MCP (GraphQL or octo-cli).",
                "none"),
            Operation.Import => (
                $"The runtime model references '{typeId}'. Knowledge pages, their versions and links are not imported " +
                "generically (no content guard, no version, no author); write them with the knowledge tools " +
                "(knowledge_upsert_page, knowledge_link). Nothing was imported.",
                "knowledge_upsert_page,knowledge_link"),
            _ => (
                $"'{typeId}' is written only through the knowledge tools, which run the content guard and record a version " +
                "with author and time: knowledge_upsert_page (content), knowledge_set_status (status), " +
                "knowledge_restore_version (restore), knowledge_link (links). Nothing was written.",
                WriteTools)
        };
    }
}
