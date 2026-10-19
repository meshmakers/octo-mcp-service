using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Resolves the <c>ckTypeId</c> a caller passes to a type tool against the tenant's CK cache (AB#6998).
/// </summary>
/// <remarks>
///     <para>
///         The canonical id is the full id <c>Model-x.y.z/Type-n</c> that <c>search_types</c>,
///         <c>get_available_types</c> and <c>get_type_schema</c> return. The resolver additionally accepts the
///         shorter spellings the tools used to hand out or parse, as long as they name exactly one installed type:
///         the runtime id <c>Model/Type-n</c> (model without version), the major-qualified model
///         <c>Model-2/Type-n</c> and the element without its version <c>Model-2.6.0/Type</c>.
///     </para>
///     <para>
///         An id that matches nothing, or more than one installed type, yields an error that names the full ids of
///         the candidates instead of the bare "not found in CkCache".
///     </para>
/// </remarks>
internal static class CkTypeIdResolver
{
    private const int MaxCandidates = 5;

    internal sealed record Resolution(CkTypeGraph? Type, string? Error);

    internal static Resolution Resolve(ICkCacheService cache, string tenantId, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new Resolution(null, "ckTypeId is required.");
        }

        var text = input.Trim();
        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
        {
            return new Resolution(null,
                $"ckTypeId '{input}' must have the form 'Model-<version>/Type-<n>', " +
                "e.g. 'Industry.Basic-2.6.0/Machine-1'. Use search_types to find the id.");
        }

        // Exact spellings first: the runtime id (model without version) and the full id.
        if (TryExact(cache, tenantId, text) is { } exact)
        {
            return new Resolution(exact, null);
        }

        var (modelName, modelVersion) = SplitModel(text[..slash]);
        var (elementName, elementVersion) = SplitElement(text[(slash + 1)..]);

        var types = cache.GetCkTypes(tenantId).ToList();
        var matches = types
            .Where(t => string.Equals(t.CkTypeId.ModelId.Name, modelName, StringComparison.Ordinal)
                        && string.Equals(t.CkTypeId.ElementId.Name, elementName, StringComparison.Ordinal)
                        && ModelVersionMatches(t.CkTypeId.ModelId.Version.ToString(), modelVersion)
                        && (elementVersion == null || t.CkTypeId.ElementId.Version == elementVersion))
            .ToList();

        if (matches.Count == 1)
        {
            return new Resolution(matches[0], null);
        }

        if (matches.Count > 1)
        {
            return new Resolution(null,
                $"ckTypeId '{input}' is ambiguous in tenant '{tenantId}'. Use one of the full ids: " +
                $"{string.Join(", ", matches.Select(Full).OrderBy(s => s, StringComparer.Ordinal).Take(MaxCandidates))}.");
        }

        var candidates = types
            .Where(t => string.Equals(t.CkTypeId.ElementId.Name, elementName, StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(t.CkTypeId.ModelId.Name, modelName, StringComparison.Ordinal)
                            && t.CkTypeId.ElementId.Name.Contains(elementName, StringComparison.OrdinalIgnoreCase)))
            .Select(Full)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        var error = $"CkTypeId '{input}' not found in the Construction Kit cache of tenant '{tenantId}'.";
        error += candidates.Count > 0
            ? $" Did you mean: {string.Join(", ", candidates)}?"
            : " Use search_types to find the full id (e.g. 'Industry.Basic-2.6.0/Machine-1').";
        return new Resolution(null, error);
    }

    // The cache getters throw for an unknown id, so a miss is any failure; the scan below then explains it.
    private static CkTypeGraph? TryExact(ICkCacheService cache, string tenantId, string text)
    {
        try
        {
            if (cache.GetRtCkType(tenantId, new RtCkId<CkTypeId>(text)) is { } rtType)
            {
                return rtType;
            }
        }
        catch (Exception)
        {
            // not a known runtime id; try the next spelling
        }

        try
        {
            if (cache.GetCkType(tenantId, new CkId<CkTypeId>(text)) is { } ckType)
            {
                return ckType;
            }
        }
        catch (Exception)
        {
            // not a known full id; fall through to the scan
        }

        return null;
    }

    /// <summary>The id the runtime (<c>RtCkId</c>) based engine calls take: model without version.</summary>
    internal static string ToRtCkId(CkTypeGraph type) => type.CkTypeId.ToRtCkId().ToString();

    /// <summary>The canonical full id <c>Model-x.y.z/Type-n</c>.</summary>
    internal static string Full(CkTypeGraph type) => type.CkTypeId.ToString();

    private static bool ModelVersionMatches(string installed, string? requested)
    {
        return requested == null
               || string.Equals(installed, requested, StringComparison.Ordinal)
               || installed.StartsWith(requested + ".", StringComparison.Ordinal);
    }

    private static (string Name, string? Version) SplitModel(string model)
    {
        var dash = model.IndexOf('-');
        return dash < 0 ? (model, null) : (model[..dash], model[(dash + 1)..]);
    }

    private static (string Name, uint? Version) SplitElement(string element)
    {
        var dash = element.LastIndexOf('-');
        if (dash > 0 && uint.TryParse(element[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return (element[..dash], v);
        }

        return (element, null);
    }
}
