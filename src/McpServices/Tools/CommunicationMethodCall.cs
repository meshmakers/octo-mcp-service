using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Communication.v4;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Outcome of a communication write that ran as a CK method call.</summary>
/// <param name="IsSuccess">The write was done.</param>
/// <param name="Value">The result value of the method; <c>default</c> without one.</param>
/// <param name="Error">
///     The refusal as <c>CODE: message (params)</c> (<see cref="IdentityMethodCall.Describe" />), or the text of a method the
///     communication controller does not offer; <c>null</c> on success. Passed through <see cref="ErrorTextRedactor" />.
/// </param>
/// <param name="ErrorCode">The code of the refusal (declared or platform error); <c>null</c> on success.</param>
/// <param name="ErrorMessage">The message of a declared error as the server sent it; <c>null</c> otherwise.</param>
/// <param name="IsUnavailable">The controller does not offer the method (or no handler is live); nothing was changed.</param>
internal sealed record CommunicationMethodOutcome<T>(bool IsSuccess, T? Value, string? Error, string? ErrorCode = null,
    string? ErrorMessage = null, bool IsUnavailable = false);

/// <summary>
///     Communication wave 3: runs a communication write (pipeline deploy, execute, debug and move, data flow, workload,
///     deployment site and trigger operations, queued execution cancel) through a generated CK method client of
///     <c>System.Communication</c> on the tenant client. There is no REST fallback:
///     <list type="bullet">
///         <item>
///             Not available (<see cref="IdentityMethodCall.IsMethodUnavailable" />: no live handler, or a document the
///             asset repository rejects because its schema lacks the method): the tool reports that it needs communication
///             controller services with System.Communication <see cref="MinimumSystemCommunicationVersion" />; nothing was
///             changed.
///         </item>
///         <item>
///             Any other <see cref="CkMethodCallException" /> (<c>METHOD_FORBIDDEN</c> without the role
///             <c>CommunicationManagement</c>, <c>METHOD_VALIDATION_FAILED</c>, <c>METHOD_TARGET_NOT_FOUND</c>,
///             <c>METHOD_TIMEOUT</c>) and every declared error: a refusal as <c>CODE: message (params)</c>.
///         </item>
///     </list>
/// </summary>
internal static class CommunicationMethodCall
{
    /// <summary>The System.Communication version that carries every method of communication wave 3.</summary>
    internal const string MinimumSystemCommunicationVersion = "4.19";

    /// <summary>The platform error code of a call whose target entity does not exist (or is of another type).</summary>
    internal const string TargetNotFoundCode = "METHOD_TARGET_NOT_FOUND";

    /// <summary>The platform error code of a refused caller (e.g. without the role <c>CommunicationManagement</c>).</summary>
    internal const string ForbiddenCode = "METHOD_FORBIDDEN";

    private const string HandlerNotAvailableCode = "METHOD_HANDLER_NOT_AVAILABLE";

    /// <summary>
    ///     Whether an error of one pipeline of a move list ends the list: a refused caller (the same for every pipeline), or
    ///     a controller without the method while nothing was moved yet. Every other error, and a missing method after a
    ///     pipeline was moved (a controller restart during the batch), is a failed item, so the list keeps saying which
    ///     pipelines were moved.
    /// </summary>
    internal static bool EndsTheBatch<T>(CommunicationMethodOutcome<T> outcome, bool anythingMoved) =>
        outcome.ErrorCode == ForbiddenCode || (outcome.IsUnavailable && !anythingMoved);

    /// <summary>The description prefix of a sensitive method parameter (a pipeline definition or input).</summary>
    internal const string SensitiveParameterPrefix =
        "SENSITIVE: sent only in the CK method call, never echoed in the response; lines of eight or more characters " +
        "of it are removed from error texts. ";

    /// <summary>The text of a method that answered success without the result record it always returns.</summary>
    internal static string NoResultText(string method) =>
        $"The CK method System.Communication/{method} returned no result.";

    /// <summary>
    ///     The CK type of a workload by runtime id: the workload methods are declared on the abstract
    ///     <c>DeployableWorkload</c> and run through the method client of the concrete type.
    /// </summary>
    internal const string WorkloadCkTypeQuery = @"
      query getDeployableWorkloadCkTypeById($rtId: OctoObjectId!) {
        runtime {
          systemCommunicationDeployableWorkload(rtId: $rtId) {
            totalCount
            items {
              rtId
              ckTypeId
            }
          }
        }
      }";

    /// <summary>Runs a method with a result value; see the class remarks.</summary>
    internal static async Task<CommunicationMethodOutcome<T>> RunAsync<T>(Func<Task<CkMethodResult<T>>> method)
    {
        try
        {
            var result = await method();
            return result.IsSuccess
                ? new CommunicationMethodOutcome<T>(true, result.Value, null)
                : new CommunicationMethodOutcome<T>(false, default, Safe(IdentityMethodCall.Describe(result.Error!)),
                    result.Error!.Code, result.Error.Message);
        }
        catch (Exception ex) when (IdentityMethodCall.IsMethodUnavailable(ex))
        {
            return new CommunicationMethodOutcome<T>(false, default, UnavailableText(ex),
                (ex as CkMethodCallException)?.Code, IsUnavailable: true);
        }
        catch (CkMethodCallException ex)
        {
            return new CommunicationMethodOutcome<T>(false, default, Safe(IdentityMethodCall.Describe(ex.Error)),
                ex.Code);
        }
    }

    /// <summary>Runs a method without a result value; see the class remarks.</summary>
    internal static async Task<CommunicationMethodOutcome<bool>> RunAsync(Func<Task<CkMethodResult>> method) =>
        await RunAsync<bool>(async () =>
        {
            var result = await method();
            return result.IsSuccess
                ? CkMethodResult<bool>.Success(result.InvocationId, true)
                : CkMethodResult<bool>.Failure(result.Error!);
        });

    /// <summary>
    ///     The text of a method the communication controller does not offer: it names the System.Communication version the
    ///     tool needs; nothing was changed and there is no REST fallback.
    /// </summary>
    internal static string UnavailableText(Exception exception)
    {
        var method = exception is CkMethodCallException ck ? $"the CK method {ck.MethodId}" : "this CK method";
        var noHandler = exception is CkMethodCallException { Code: HandlerNotAvailableCode };
        return $"The communication controller does not offer {method}. This tool needs communication controller " +
               $"services with System.Communication {MinimumSystemCommunicationVersion} (the communication wave 3 " +
               "methods)" +
               (noHandler
                   ? ", or no handler of the method is running right now (communication controller restarting?). "
                   : ". ") +
               "Nothing was changed; the tool does not fall back to the REST endpoint.";
    }

    /// <summary>
    ///     <c>null</c> when every id is a runtime id, otherwise the error text naming the first that is not. The method
    ///     documents declare the ids as <c>OctoObjectId</c>; the check keeps a malformed id from reaching the server.
    /// </summary>
    internal static string? InvalidIdError(params (string Name, string? Value)[] ids)
    {
        foreach (var (name, value) in ids)
        {
            if (string.IsNullOrWhiteSpace(value) || !OctoObjectId.TryParse(value, out _))
            {
                return $"{name} '{value}' is not a valid runtime id.";
            }
        }

        return null;
    }

    /// <summary>
    ///     The concrete workload type of <paramref name="workloadId" />, read with a GraphQL query of the tenant; <c>null</c>
    ///     with an error text when the workload is unknown or of another type (nothing was changed).
    /// </summary>
    internal static async Task<(WorkloadKind? Kind, string? Error)> ResolveWorkloadKindAsync(ITenantClient tenantClient,
        string workloadId)
    {
        var result = await tenantClient.SendQueryAsync<WorkloadTypeDto>(new GraphQLRequest
        {
            Query = WorkloadCkTypeQuery,
            Variables = new { rtId = workloadId }
        });
        var ckTypeId = result?.Items?.FirstOrDefault()?.CkTypeId;
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return (null, $"Workload '{workloadId}' not found.");
        }

        var kind = ParseWorkloadKind(ckTypeId);
        return kind == null
            ? (null, $"Workload '{workloadId}' is of type '{ckTypeId}', which is no Adapter, AdapterPool or Application.")
            : (kind, null);
    }

    /// <summary>
    ///     The workload kind of a CK type id such as <c>System.Communication/Adapter</c> or
    ///     <c>System.Communication/AdapterPool-2</c> (the element version suffix is ignored, a tenant may hold a newer
    ///     model); <c>null</c> for another type.
    /// </summary>
    internal static WorkloadKind? ParseWorkloadKind(string ckTypeId)
    {
        const string prefix = "System.Communication/";
        if (!ckTypeId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = ckTypeId[prefix.Length..];
        var dash = name.LastIndexOf('-');
        if (dash > 0 && dash < name.Length - 1 && name[(dash + 1)..].All(char.IsAsciiDigit))
        {
            name = name[..dash];
        }

        return name switch
        {
            "Adapter" => WorkloadKind.Adapter,
            "AdapterPool" => WorkloadKind.AdapterPool,
            "Application" => WorkloadKind.Application,
            _ => null
        };
    }

    /// <summary>Runs a workload method on the client of the workload's concrete type.</summary>
    internal static async Task<CommunicationMethodOutcome<T>> RunOnWorkloadAsync<T>(ITenantClient tenantClient,
        string workloadId,
        Func<RtAdapterMethodClient, Task<CkMethodResult<T>>> adapter,
        Func<RtAdapterPoolMethodClient, Task<CkMethodResult<T>>> pool,
        Func<RtApplicationMethodClient, Task<CkMethodResult<T>>> application)
    {
        var (kind, error) = await ResolveWorkloadKindAsync(tenantClient, workloadId);
        if (kind == null)
        {
            return new CommunicationMethodOutcome<T>(false, default, error, TargetNotFoundCode);
        }

        return await RunAsync(() => kind switch
        {
            WorkloadKind.Adapter => adapter(new RtAdapterMethodClient(tenantClient)),
            WorkloadKind.AdapterPool => pool(new RtAdapterPoolMethodClient(tenantClient)),
            _ => application(new RtApplicationMethodClient(tenantClient))
        });
    }

    /// <summary>Runs a workload method without a result value on the client of the workload's concrete type.</summary>
    internal static Task<CommunicationMethodOutcome<bool>> RunOnWorkloadAsync(ITenantClient tenantClient,
        string workloadId,
        Func<RtAdapterMethodClient, Task<CkMethodResult>> adapter,
        Func<RtAdapterPoolMethodClient, Task<CkMethodResult>> pool,
        Func<RtApplicationMethodClient, Task<CkMethodResult>> application) =>
        RunOnWorkloadAsync<bool>(tenantClient, workloadId,
            async c => ToBool(await adapter(c)), async c => ToBool(await pool(c)),
            async c => ToBool(await application(c)));

    private static CkMethodResult<bool> ToBool(CkMethodResult result) =>
        result.IsSuccess
            ? CkMethodResult<bool>.Success(result.InvocationId, true)
            : CkMethodResult<bool>.Failure(result.Error!);

    private static string Safe(string text) => ErrorTextRedactor.Redact(text);

    /// <summary>
    ///     The error text of a call with a sensitive parameter (the pipeline definition of a deploy, the input of an
    ///     execution): the value as a whole and each of its lines of eight or more characters are replaced by
    ///     <c>[REDACTED]</c>, the rule the communication controller applies to its own messages. Shorter lines are left,
    ///     they would garble the text and carry no credential.
    /// </summary>
    internal static string? ScrubSensitive(string? text, string? value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
        {
            return text;
        }

        var result = value.Length >= 8 ? text.Replace(value, "[REDACTED]", StringComparison.Ordinal) : text;
        foreach (var line in value.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 8)
                     .OrderByDescending(l => l.Length))
        {
            result = result.Replace(line, "[REDACTED]", StringComparison.Ordinal);
        }

        return ErrorTextRedactor.Redact(result);
    }

    /// <summary>The concrete types of <c>System.Communication/DeployableWorkload</c>.</summary>
    internal enum WorkloadKind
    {
        Adapter,
        AdapterPool,
        Application
    }

    /// <summary>The fields of the workload type query.</summary>
    internal sealed class WorkloadTypeDto
    {
        /// <summary>The runtime id of the workload.</summary>
        public string? RtId { get; set; }

        /// <summary>The CK type id the server answered (element-versioned, e.g. <c>System.Communication/Adapter-1</c>).</summary>
        public string? CkTypeId { get; set; }
    }
}
