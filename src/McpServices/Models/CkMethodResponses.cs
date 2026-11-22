using System.Text.Json;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Result of <c>invoke_ck_method</c>: a success with the invocation id and the declared result (or the operation
///     handle of an asynchronous method), or a refusal. A refusal of the server carries <see cref="MethodError" /> with
///     the same code, parameters and details a GraphQL caller reads from <c>extensions</c>; a refusal of the tool
///     itself (arguments, unknown method, not allowed) carries only <see cref="ErrorMessage" />.
/// </summary>
public sealed class InvokeCkMethodResponse
{
    /// <summary>True when the method ran (or, for an asynchronous method, was accepted).</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Why the call failed; for a refusal of the server <c>"{code}: {message}"</c>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Additional information.</summary>
    public string? Message { get; set; }

    /// <summary>The tenant the method was called on.</summary>
    public string? TenantId { get; set; }

    /// <summary>The resolved runtime CK type id the method was called on.</summary>
    public string? CkTypeId { get; set; }

    /// <summary>The qualified id of the method (declaring type and method id).</summary>
    public string? MethodId { get; set; }

    /// <summary>The invocation id of the call.</summary>
    public string? InvocationId { get; set; }

    /// <summary>The declared result of a synchronous method, as JSON; <c>null</c> without a declared result.</summary>
    public JsonElement? Result { get; set; }

    /// <summary>The operation handle of an asynchronous method.</summary>
    public CkMethodOperationInfo? Operation { get; set; }

    /// <summary>The structured error of a call the server refused; <c>null</c> on success or a refusal of the tool.</summary>
    public CkMethodErrorInfo? MethodError { get; set; }
}

/// <summary>
///     The error of a refused CK method call, exactly as the asset repository writes it into the GraphQL error's
///     <c>extensions</c> (<c>code</c>, <c>parameters</c>, <c>details</c>).
/// </summary>
public sealed class CkMethodErrorInfo
{
    /// <summary>The error code (<c>extensions.code</c>), e.g. a declared code such as <c>CONCURRENT_CHANGE</c> or a platform code such as <c>METHOD_FORBIDDEN</c>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>True when the method declares the code in its <c>errors</c>; false for platform codes.</summary>
    public bool Declared { get; set; }

    /// <summary>The message of the GraphQL error.</summary>
    public string? Message { get; set; }

    /// <summary>The error parameters (<c>extensions.parameters</c>).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The detail errors (<c>extensions.details</c>), e.g. the failed rules of a validation error.</summary>
    public List<CkMethodErrorDetailInfo> Details { get; set; } = [];
}

/// <summary>
///     One detail error of a refused CK method call.
/// </summary>
public sealed class CkMethodErrorDetailInfo
{
    /// <summary>The detail code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The detail parameters.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
///     The operation handle of an asynchronous CK method call.
/// </summary>
public sealed class CkMethodOperationInfo
{
    /// <summary>The operation id.</summary>
    public string? OperationId { get; set; }

    /// <summary>When the operation was accepted (UTC).</summary>
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>Seconds to wait before the first poll.</summary>
    public int? RetryAfterSeconds { get; set; }

    /// <summary>When the operation result expires (UTC).</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
