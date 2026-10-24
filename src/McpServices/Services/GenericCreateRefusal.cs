using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     The MCP refusal of the generic create for a CK type that has a static method with
///     <c>replacesGenericCreate: true</c> (declared on the type or inherited), the MCP counterpart of the GraphQL
///     refusal of the asset repository. The lookup itself is the shared engine helper
///     <see cref="GenericCreateReplacement" /> (AB#7121, AB#7448); this class only shapes the MCP response. Same
///     machine-readable contract: code <see cref="Code" /> and parameters <see cref="CkTypeIdParameter" /> and
///     <see cref="MethodIdParameter" />. The message carries ids only, never entity data or secret values.
/// </summary>
internal static class GenericCreateRefusal
{
    /// <summary>Error code of the refusal (identical to the GraphQL error code).</summary>
    public const string Code = GenericCreateReplacement.ReasonToken;

    /// <summary>Parameter with the CK type the caller tried to create.</summary>
    public const string CkTypeIdParameter = "ckTypeId";

    /// <summary>Parameter with the model-version-less qualified id of the replacing method.</summary>
    public const string MethodIdParameter = "methodId";

    /// <summary>
    ///     The refusal for <paramref name="ckTypeId" />, or <c>null</c> when the generic create is allowed. A type that
    ///     is not in the cache is not refused here (the create fails on its own).
    /// </summary>
    public static (string Message, IReadOnlyDictionary<string, string> Parameters)? TryGetRefusal(
        ICkCacheService ckCache, string tenantId, string ckTypeId)
    {
        RtCkId<CkTypeId> rtCkTypeId;
        try
        {
            rtCkTypeId = new RtCkId<CkTypeId>(ckTypeId);
        }
        catch (Exception)
        {
            return null;
        }

        if (!GenericCreateReplacement.TryGetReplacingMethod(ckCache, tenantId, rtCkTypeId, out var replacing))
        {
            return null;
        }

        var typeId = rtCkTypeId.SemanticVersionedFullName;
        var methodId = replacing.QualifiedMethodId;
        return ($"The generic create of '{typeId}' is replaced by the CK method '{methodId}'. Call the method instead.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CkTypeIdParameter] = typeId,
                [MethodIdParameter] = methodId
            });
    }
}
