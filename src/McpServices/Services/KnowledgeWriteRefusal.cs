using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8568: the generic entity tools never write the knowledge types of System.Ai (<c>KnowledgePage</c>,
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
internal static partial class KnowledgeWriteRefusal
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
        new("System.Ai/KnowledgePageVersion")
    ];

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

        return Build(runtimeId.ToString(), operation);
    }

    /// <summary>
    ///     The refusal for a runtime-model import whose file (YAML, JSON, or a zip of them) mentions a knowledge type or
    ///     a knowledge association role, or <c>null</c>. The check is by text, fail closed: any occurrence of the type
    ///     or role id refuses the whole import, before the file reaches the asset repository.
    /// </summary>
    public static (string Message, IReadOnlyDictionary<string, string> Parameters)? TryGetImportRefusal(string filePath)
    {
        var hit = FindKnowledgeReference(filePath);
        return hit == null ? null : Build(hit, Operation.Import);
    }

    /// <summary>The first knowledge type or role id found in the file, or <c>null</c>.</summary>
    internal static string? FindKnowledgeReference(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        if (IsZip(filePath))
        {
            using var archive = ZipFile.OpenRead(filePath);
            foreach (var entry in archive.Entries.Where(e => e.Length > 0))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var hit = Find(reader.ReadToEnd());
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        return Find(File.ReadAllText(filePath, Encoding.UTF8));
    }

    private static string? Find(string text)
    {
        var match = KnowledgeIdPattern().Match(text);
        return match.Success ? $"System.Ai/{match.Groups["name"].Value}" : null;
    }

    private static bool IsZip(string filePath)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(filePath);
        return stream.Read(header) == 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4;
    }

    private static bool IsKnowledgeType(ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> runtimeId)
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
        Operation operation)
    {
        var (message, tool) = operation switch
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

        return (message, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CkTypeIdParameter] = typeId,
            [ToolParameter] = tool
        });
    }

    // Runtime ids (System.Ai/KnowledgePage) and full ids (System.Ai-4.7.0/KnowledgePage-1) of the protected types and of
    // the knowledge association roles. KnowledgePageVersion is matched by its own name (the alternation is ordered).
    [GeneratedRegex(@"System\.Ai(?:-[0-9][0-9.]*)?/(?<name>KnowledgePageVersion|KnowledgePage|LinksTo|GeneratedFrom|VersionOf)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnowledgeIdPattern();
}
