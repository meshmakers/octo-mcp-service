using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Turns the arguments of <c>invoke_ck_method</c> into exactly one GraphQL method mutation of the tenant's
///     asset repository, the same document the generated SDK method clients send (octo-sdk
///     <c>MethodClientCodeGenerator</c>, asset repository <c>CkMethodMutationFields</c>).
///     <para>
///         Security invariant: the planner never widens what the caller may do. Every name in the document comes from
///         the resolved CK definition and must match the GraphQL name pattern; the caller's <c>rtId</c>, parameters and
///         idempotency key only ever go into the variables. Methods declared by a <c>System*</c> model (they have
///         dedicated tools with their own risk class and confirm gate) and methods with a sensitive result are refused,
///         judged from the resolved definition, never from the input text. Anything that cannot be resolved is refused
///         (fail closed).
///     </para>
/// </summary>
internal static partial class CkMethodInvocationPlanner
{
    /// <summary>The largest <c>parameters</c> JSON accepted, in UTF-8 bytes.</summary>
    public const int MaxParametersBytes = 256 * 1024;

    /// <summary>The deepest nesting of the <c>parameters</c> JSON accepted.</summary>
    public const int MaxParametersDepth = 32;

    /// <summary>The longest idempotency key the asset repository accepts.</summary>
    public const int MaxIdempotencyKeyLength = 128;

    private const int MaxResultSelectionDepth = 8;

    /// <summary>
    ///     Validates the arguments that need no CK definition (before any tenant, cache or network access).
    /// </summary>
    /// <returns>The error text, or <c>null</c> when the arguments have the right shape.</returns>
    public static string? ValidateArguments(string? ckTypeId, string? methodId, string? rtId, string? parameters,
        string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return "ckTypeId is required.";
        }

        if (string.IsNullOrWhiteSpace(methodId))
        {
            return "methodId is required.";
        }

        if (rtId != null && !ObjectIdPattern().IsMatch(rtId))
        {
            return $"rtId '{Shorten(rtId)}' is not a runtime id (24 hexadecimal characters).";
        }

        if (idempotencyKey != null && (idempotencyKey.Length == 0 || idempotencyKey.Length > MaxIdempotencyKeyLength))
        {
            return $"idempotencyKey must have 1 to {MaxIdempotencyKeyLength} characters.";
        }

        if (parameters != null && Encoding.UTF8.GetByteCount(parameters) > MaxParametersBytes)
        {
            return $"parameters exceed the maximum of {MaxParametersBytes} bytes.";
        }

        return null;
    }

    /// <summary>
    ///     Parses <paramref name="parameters" /> as one JSON object.
    /// </summary>
    /// <returns>The object (cloned, independent of the parser), <c>null</c> for no parameters, or an error.</returns>
    public static (JsonElement? Input, string? Error) ParseParameters(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(parameters,
                new JsonDocumentOptions { MaxDepth = MaxParametersDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "parameters must be a JSON object with one property per method parameter.");
            }

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException e)
        {
            return (null, $"parameters are not valid JSON: {e.Message}");
        }
    }

    /// <summary>
    ///     Resolves the type and the method and builds the GraphQL document. Refuses everything the tool must not call.
    /// </summary>
    public static (CkMethodInvocationPlan? Plan, string? Error) Plan(ICkCacheService cache, string tenantId,
        string ckTypeId, string methodId, string? rtId, bool hasParameters)
    {
        var resolution = CkTypeIdResolver.Resolve(cache, tenantId, ckTypeId);
        if (resolution.Type == null)
        {
            return (null, resolution.Error ?? $"ckTypeId '{Shorten(ckTypeId)}' could not be resolved.");
        }

        var type = resolution.Type;
        var rtTypeId = type.CkTypeId.ToRtCkId();
        if (type.IsAbstract)
        {
            return (null,
                $"CK type '{rtTypeId.SemanticVersionedFullName}' is abstract and has no method fields; call the method on a concrete type.");
        }

        var method = FindMethod(type, methodId.Trim());
        if (method == null)
        {
            var available = type.AllMethods.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(20).ToList();
            return (null,
                $"CK type '{rtTypeId.SemanticVersionedFullName}' has no method '{Shorten(methodId)}'." +
                (available.Count > 0
                    ? $" Methods of the type: {string.Join(", ", available)}."
                    : " The type declares no methods."));
        }

        if (IsSystemModel(type.CkTypeId.ModelId.Name) || IsSystemModel(method.DeclaringCkTypeId.ModelId.Name))
        {
            return (null,
                $"Method '{method.QualifiedMethodId}' belongs to a System model and is not callable through invoke_ck_method. " +
                "Use the dedicated tool of that area (e.g. the user, role, client or communication tools).");
        }

        // Knowledge pages (and types derived from them) are written only by the knowledge tools, through the content
        // guard - also when a custom method of a derived type would write them.
        if (type.GetBaseTypes(true).Any(t => KnowledgeWriteRefusal.ProtectedTypes.Contains(t.ToRtCkId())))
        {
            return (null,
                $"Method '{method.QualifiedMethodId}' belongs to a knowledge page type and is not callable through invoke_ck_method. " +
                "Write knowledge pages with knowledge_upsert_page.");
        }

        if (method.HasSensitiveResult)
        {
            return (null,
                $"Method '{method.QualifiedMethodId}' returns a sensitive result and is not callable through invoke_ck_method, " +
                "so the value never lands in an agent transcript.");
        }

        var definition = method.Definition;
        if (definition.Parameters?.Any(p => p.Sensitive) == true)
        {
            return (null,
                $"Method '{method.QualifiedMethodId}' takes a sensitive parameter and is not callable through invoke_ck_method, " +
                "so the value never lands in an agent transcript or an error text.");
        }

        var isInstance = definition.Kind == CkMethodKindDto.Instance;
        if (isInstance && rtId == null)
        {
            return (null, $"Method '{method.QualifiedMethodId}' is an instance method: rtId of the target entity is required.");
        }

        if (!isInstance && rtId != null)
        {
            return (null, $"Method '{method.QualifiedMethodId}' is a static method: do not pass rtId.");
        }

        var methodParameters = definition.Parameters ?? [];
        if (hasParameters && methodParameters.Count == 0)
        {
            return (null, $"Method '{method.QualifiedMethodId}' takes no parameters.");
        }

        var className = CkMethodIds.MethodClassName(definition.MethodId);
        var typeField = CamelCase(PascalCase(rtTypeId.SemanticVersionedFullName)) + "s";
        var fieldName = CamelCase(className);
        var inputType = PascalCase(method.DeclaringCkTypeId.ToRtCkId().SemanticVersionedFullName) + className +
                        "MethodInput";
        var operationName = PascalCase(rtTypeId.SemanticVersionedFullName) + className;
        foreach (var name in new[] { typeField, fieldName, inputType, operationName })
        {
            if (!GraphQlNamePattern().IsMatch(name))
            {
                // Cannot happen for a valid CK model; refused instead of being written into the document.
                return (null, $"Method '{method.QualifiedMethodId}' has no valid GraphQL name ('{Shorten(name)}').");
            }
        }

        var hasInput = methodParameters.Count > 0;
        var inputRequired = methodParameters.Any(p => !p.IsOptional);
        var declarations = new List<string>();
        var arguments = new List<string>();
        if (isInstance)
        {
            declarations.Add("$rtId: OctoObjectId!");
            arguments.Add("rtId: $rtId");
        }

        if (hasInput)
        {
            declarations.Add($"$input: {inputType}{(inputRequired ? "!" : string.Empty)}");
            arguments.Add("input: $input");
        }

        declarations.Add("$idempotencyKey: String");
        arguments.Add("idempotencyKey: $idempotencyKey");

        var document = new StringBuilder();
        document.Append($"mutation {operationName}({string.Join(", ", declarations)}) {{ ");
        document.Append($"runtime {{ {typeField} {{ {fieldName}({string.Join(", ", arguments)}) {{ invocationId");
        if (method.IsAsync)
        {
            document.Append(" operation { operationId acceptedAtUtc retryAfterSeconds expiresAtUtc }");
        }
        else if (definition.Result != null)
        {
            document.Append(" result");
            document.Append(Selection(cache, tenantId, definition.Result.ValueType, definition.Result.ValueCkRecordId,
                0));
        }

        document.Append(" } } } }");

        var declaredErrorCodes = (definition.Errors ?? [])
            .Select(e => e.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (new CkMethodInvocationPlan(rtTypeId.SemanticVersionedFullName, method.QualifiedMethodId,
            document.ToString(), operationName, isInstance, hasInput, method.IsAsync, declaredErrorCodes), null);
    }

    /// <summary>
    ///     A model is a System model when its name starts with <c>System</c> in any case (fail closed: also
    ///     <c>system.identity</c> or <c>SYSTEM</c>).
    /// </summary>
    internal static bool IsSystemModel(string? modelName) =>
        modelName == null || modelName.StartsWith("System", StringComparison.OrdinalIgnoreCase);

    private static CkMethodGraph? FindMethod(CkTypeGraph type, string methodId)
    {
        if (type.AllMethods.TryGetValue(methodId, out var exact))
        {
            return exact;
        }

        // A qualified id ("Model/Type.Method") of a method of this type, as the error texts and GraphQL descriptions
        // name it.
        return type.AllMethods.Values.FirstOrDefault(m =>
            string.Equals(m.QualifiedMethodId, methodId, StringComparison.Ordinal));
    }

    private static string Selection(ICkCacheService cache, string tenantId, AttributeValueTypesDto valueType,
        CkId<CkRecordId>? recordId, int depth)
    {
        switch (valueType)
        {
            case AttributeValueTypesDto.Secret:
                return " { isSet keyMissing setAt }";
            case AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray when recordId != null:
                if (depth > MaxResultSelectionDepth || !cache.TryGetCkRecord(tenantId, recordId, out var record) ||
                    record == null)
                {
                    return " { __typename }";
                }

                var fields = new List<string>();
                foreach (var attribute in record.AllAttributes.Values.OrderBy(a => a.AttributeName,
                             StringComparer.Ordinal))
                {
                    // Same rules as the asset repository and the SDK generator: hidden attributes are not in the
                    // GraphQL types, BinaryLinked and GeospatialPoint are not mapped.
                    if (!AttributeAccess.IsExposedInOutput(attribute.Access) ||
                        attribute.ValueType is AttributeValueTypesDto.BinaryLinked
                            or AttributeValueTypesDto.GeospatialPoint)
                    {
                        continue;
                    }

                    var name = CamelCase(attribute.AttributeName);
                    if (!GraphQlNamePattern().IsMatch(name))
                    {
                        continue;
                    }

                    fields.Add(name + Selection(cache, tenantId, attribute.ValueType, attribute.ValueCkRecordId,
                        depth + 1));
                }

                return fields.Count == 0 ? " { __typename }" : $" {{ {string.Join(" ", fields)} }}";
            default:
                return string.Empty;
        }
    }

    private static string PascalCase(string semanticVersionedFullName)
    {
        var s = semanticVersionedFullName.Replace(".", "").Replace("/", "").Replace("-", "");
        return string.IsNullOrWhiteSpace(s) ? string.Empty : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string CamelCase(string s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : char.ToLowerInvariant(s[0]) + s[1..];

    private static string Shorten(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length <= 80 ? oneLine : oneLine[..80] + "...";
    }

    [GeneratedRegex("^[0-9a-fA-F]{24}$")]
    private static partial Regex ObjectIdPattern();

    [GeneratedRegex("^[_A-Za-z][_0-9A-Za-z]*$")]
    private static partial Regex GraphQlNamePattern();
}

/// <summary>
///     The one GraphQL method mutation <c>invoke_ck_method</c> sends.
/// </summary>
/// <param name="CkTypeId">The resolved runtime type id (semantic versioned full name)</param>
/// <param name="QualifiedMethodId">The qualified id of the method (declaring type and method id)</param>
/// <param name="Document">The GraphQL document</param>
/// <param name="OperationName">The operation name of the document</param>
/// <param name="IsInstance">The method needs <c>rtId</c></param>
/// <param name="HasInput">The method has parameters (the document declares <c>$input</c>)</param>
/// <param name="IsAsync">The method runs asynchronously and answers with an operation handle</param>
/// <param name="DeclaredErrorCodes">The error codes the method declares</param>
internal sealed record CkMethodInvocationPlan(
    string CkTypeId,
    string QualifiedMethodId,
    string Document,
    string OperationName,
    bool IsInstance,
    bool HasInput,
    bool IsAsync,
    IReadOnlyCollection<string> DeclaredErrorCodes);
