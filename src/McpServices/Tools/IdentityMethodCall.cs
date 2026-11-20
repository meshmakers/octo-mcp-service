using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Outcome of an identity write that ran as a CK method call.</summary>
/// <param name="IsSuccess">The write was done (or the method answered a declared error the REST endpoint treats as done).</param>
/// <param name="Value">The result value of the method; <c>default</c> without one.</param>
/// <param name="Error">
///     The refusal as <c>CODE: message (params)</c> (<see cref="IdentityMethodCall.Describe" />), or the text of a method the
///     identity service does not offer; <c>null</c> on success. Not yet redacted: the tool passes it through
///     <see cref="IdentityMethodCall.SafeText" />.
/// </param>
/// <param name="ErrorCode">The code of the refusal (declared or platform error); <c>null</c> on success.</param>
internal sealed record IdentityMethodOutcome<T>(bool IsSuccess, T? Value, string? Error, string? ErrorCode = null);

/// <summary>
///     Identity wave 2: runs an identity write through a generated CK method client of <c>System.Identity</c>. There is no
///     REST fallback (decided for every wave 2 tool, with or without a secret):
///     <list type="bullet">
///         <item>
///             Not available (<see cref="IsMethodUnavailable" />): no live handler (<c>METHOD_HANDLER_NOT_AVAILABLE</c>), a
///             document the asset repository rejects in GraphQL validation (every error carries <c>extensions.number</c>), or
///             a target whose runtime id the identity service does not return (<see cref="RtIdNotResolvedException" />). The
///             tool reports that it needs identity services with System.Identity <see cref="MinimumSystemIdentityVersion" />
///             (and, for a secret-carrying method, the bus switch <c>Methods:Identity:EnablePasswordMethods</c>); nothing
///             was changed.
///         </item>
///         <item>
///             Any other <see cref="CkMethodCallException" /> (<c>METHOD_FORBIDDEN</c>, <c>METHOD_VALIDATION_FAILED</c>,
///             <c>METHOD_TARGET_NOT_FOUND</c>, …) and every declared error: a refusal as <c>CODE: message (params)</c>,
///             except the declared errors the identity REST endpoint answered with success (e.g. <c>MEMBER_ALREADY_IN_GROUP</c>),
///             which the tool passes as <c>successCodes</c>.
///         </item>
///     </list>
///     Exceptions of the lookups propagate to the tool, which reports them through <see cref="SafeText" />.
/// </summary>
internal static class IdentityMethodCall
{
    /// <summary>The System.Identity version of the identity wave 2 methods.</summary>
    internal const string MinimumSystemIdentityVersion = "3.8";

    /// <summary>Runs a method with a result value; see the class remarks.</summary>
    /// <param name="method">The lookup(s) and the method call.</param>
    /// <param name="secretMethod">
    ///     The method carries a secret: its handler is offered on the method bus only with
    ///     <c>Methods:Identity:EnablePasswordMethods</c> on, which the text of an unavailable method names.
    /// </param>
    /// <param name="successCodes">Declared error codes the former REST endpoint answered with success.</param>
    internal static async Task<IdentityMethodOutcome<T>> RunAsync<T>(
        Func<Task<CkMethodResult<T>>> method,
        bool secretMethod = false,
        params string[] successCodes)
    {
        try
        {
            var result = await method();
            if (result.IsSuccess)
            {
                return new IdentityMethodOutcome<T>(true, result.Value, null);
            }

            return successCodes.Contains(result.Error!.Code, StringComparer.Ordinal)
                ? new IdentityMethodOutcome<T>(true, default, null)
                : new IdentityMethodOutcome<T>(false, default, Describe(result.Error), result.Error.Code);
        }
        catch (Exception ex) when (IsMethodUnavailable(ex))
        {
            return new IdentityMethodOutcome<T>(false, default, UnavailableText(ex, secretMethod),
                (ex as CkMethodCallException)?.Code);
        }
        catch (CkMethodCallException ex)
        {
            return new IdentityMethodOutcome<T>(false, default, Describe(ex.Error), ex.Code);
        }
    }

    /// <summary>Runs a method without a result value (or whose value the tool does not use); see the class remarks.</summary>
    internal static async Task<IdentityMethodOutcome<bool>> RunAsync(
        Func<Task<CkMethodResult>> method,
        bool secretMethod = false,
        params string[] successCodes) =>
        await RunAsync<bool>(async () =>
        {
            var result = await method();
            return result.IsSuccess
                ? CkMethodResult<bool>.Success(result.InvocationId, true)
                : CkMethodResult<bool>.Failure(result.Error!);
        }, secretMethod, successCodes);

    /// <summary>
    ///     The GraphQL validation codes of a schema that lacks the method (unknown field, type or argument). A variable the
    ///     server cannot coerce (e.g. a malformed runtime id, <c>INVALID_VALUE</c>) is a validation error too, but it says
    ///     nothing about the identity version, so it is not among them.
    /// </summary>
    private static readonly HashSet<string> SchemaLacksMethodCodes = new(StringComparer.Ordinal)
    {
        "FIELDS_ON_CORRECT_TYPE", "KNOWN_TYPE_NAMES", "KNOWN_ARGUMENT_NAMES"
    };

    /// <summary>
    ///     The call was certainly not executed because the identity service does not offer the method: no live handler, a
    ///     document the schema rejects because it lacks the method (every GraphQL error carries the spec section
    ///     <c>extensions.number</c> and at least one names an unknown field, type or argument), or a target whose runtime id
    ///     could not be resolved. Other validation errors (a malformed id) are reported as they are.
    /// </summary>
    internal static bool IsMethodUnavailable(Exception exception) => exception switch
    {
        RtIdNotResolvedException => true,
        CkMethodCallException ck when string.Equals(ck.Code, "METHOD_HANDLER_NOT_AVAILABLE", StringComparison.Ordinal) =>
            true,
        QlQueryErrorException ql => ql.Errors is { Count: > 0 } errors &&
                                    errors.All(e => e.Extensions != null && e.Extensions.ContainsKey("number")) &&
                                    errors.Any(e => e.Extensions!.TryGetValue("code", out var code) &&
                                                    code is string c && SchemaLacksMethodCodes.Contains(c)),
        _ => false
    };

    /// <summary>
    ///     The text of a method the identity service does not offer: it names the identity version the tool needs and, for a
    ///     secret-carrying method, the bus switch; nothing was changed and there is no REST fallback.
    /// </summary>
    internal static string UnavailableText(Exception exception, bool secretMethod)
    {
        var method = exception is CkMethodCallException ck ? $"the CK method {ck.MethodId}" : "this CK method";
        var switchText = secretMethod
            ? " with the secret-carrying methods switched on (Methods:Identity:EnablePasswordMethods)"
            : string.Empty;
        return $"The identity service does not offer {method}. This tool needs identity services with System.Identity " +
               $"{MinimumSystemIdentityVersion} (the identity wave 2 methods){switchText}. Nothing was changed; the tool " +
               "does not fall back to the REST endpoint.";
    }

    /// <summary>
    ///     <c>CODE: message (name=value, …)</c>, followed by the codes of the error's details in brackets; the parameters of the
    ///     error are listed in parentheses when it has any.
    /// </summary>
    internal static string Describe(CkMethodError error)
    {
        var text = string.IsNullOrWhiteSpace(error.Message) ? error.Code : $"{error.Code}: {error.Message}";
        if (error.Parameters.Count > 0)
        {
            text += $" ({string.Join(", ", error.Parameters.Select(p => $"{p.Key}={p.Value}"))})";
        }

        var codes = error.Details.Select(d => d.Code).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        return codes.Count == 0 ? text : $"{text} [{string.Join(", ", codes)}]";
    }

    /// <summary>
    ///     The text a tool may return: every given secret replaced by <c>[REDACTED]</c>, then credentials a server echoed
    ///     removed (<see cref="ErrorTextRedactor" />).
    /// </summary>
    internal static string SafeText(string? text, params string?[] secrets)
    {
        var result = text ?? string.Empty;
        foreach (var secret in secrets)
        {
            result = UserManagementTools.Scrub(result, secret);
        }

        return ErrorTextRedactor.Redact(result);
    }
}

/// <summary>
///     The runtime id of a method target could not be resolved (an identity service without <c>rtId</c> in its REST answer):
///     the tool reports that it needs the wave 2 identity services.
/// </summary>
internal sealed class RtIdNotResolvedException(string message) : Exception(message);
