using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.StreamData.v1;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Outcome of an archive write that ran as a CK method call.</summary>
/// <param name="IsSuccess">The write was done.</param>
/// <param name="Value">The result value of the method; <c>default</c> without one.</param>
/// <param name="Error">
///     The refusal as <c>CODE: message (params)</c> (<see cref="IdentityMethodCall.Describe" />), or the text of a method the
///     asset repository does not offer; <c>null</c> on success. Passed through <see cref="ErrorTextRedactor" />.
/// </param>
/// <param name="ErrorCode">The code of the refusal (declared or platform error); <c>null</c> on success.</param>
internal sealed record ArchiveMethodOutcome<T>(bool IsSuccess, T? Value, string? Error, string? ErrorCode = null);

/// <summary>
///     Stream-data archives: runs an archive write (lifecycle, column, rollup freeze / unfreeze / rewind, backfill) through a
///     generated CK method client of <c>System.StreamData</c> on the tenant client. There is no REST fallback:
///     <list type="bullet">
///         <item>
///             Not available (<see cref="IdentityMethodCall.IsMethodUnavailable" />: no live handler, or a document the asset
///             repository rejects because the tenant's schema lacks the method): the tool reports that it needs
///             System.StreamData <see cref="MinimumSystemStreamDataVersion" />; nothing was changed.
///         </item>
///         <item>
///             Any other <see cref="CkMethodCallException" /> (<c>METHOD_FORBIDDEN</c> without the role
///             <c>StreamDataAdmin</c>, <c>METHOD_TARGET_NOT_FOUND</c>, <c>METHOD_TIMEOUT</c>) and every declared error
///             (<c>STREAMDATA_INVALID_TRANSITION</c>, ...): a refusal as <c>CODE: message (params)</c>.
///         </item>
///     </list>
/// </summary>
internal static class ArchiveMethodCall
{
    /// <summary>The System.StreamData version that carries every Archive method the archive tools call.</summary>
    internal const string MinimumSystemStreamDataVersion = "1.30";

    /// <summary>The platform error code of a call whose target entity does not exist (or is of another type).</summary>
    internal const string TargetNotFoundCode = "METHOD_TARGET_NOT_FOUND";

    /// <summary>The declared error of an asynchronous backfill whose job ended Failed.</summary>
    internal const string RecomputeFailedCode = "STREAMDATA_RECOMPUTE_FAILED";

    private const string HandlerNotAvailableCode = "METHOD_HANDLER_NOT_AVAILABLE";

    /// <summary>
    ///     The CK type of an archive by runtime id: the lifecycle and column methods are declared on the abstract
    ///     <c>Archive</c> and run through the method client of the concrete type.
    /// </summary>
    internal const string ArchiveCkTypeQuery = @"
      query getArchiveCkTypeById($rtId: OctoObjectId!) {
        runtime {
          systemStreamDataArchive(rtId: $rtId) {
            totalCount
            items {
              rtId
              ckTypeId
            }
          }
        }
      }";

    /// <summary>Runs a method with a result value; see the class remarks.</summary>
    internal static async Task<ArchiveMethodOutcome<T>> RunAsync<T>(Func<Task<CkMethodResult<T>>> method)
    {
        try
        {
            var result = await method();
            return result.IsSuccess
                ? new ArchiveMethodOutcome<T>(true, result.Value, null)
                : new ArchiveMethodOutcome<T>(false, default, Safe(IdentityMethodCall.Describe(result.Error!)),
                    result.Error!.Code);
        }
        catch (Exception ex) when (IdentityMethodCall.IsMethodUnavailable(ex))
        {
            return new ArchiveMethodOutcome<T>(false, default, UnavailableText(ex), (ex as CkMethodCallException)?.Code);
        }
        catch (CkMethodCallException ex)
        {
            return new ArchiveMethodOutcome<T>(false, default, Safe(IdentityMethodCall.Describe(ex.Error)), ex.Code);
        }
    }

    /// <summary>
    ///     Runs an Archive method (lifecycle or column) on the client of the archive's concrete type; an unknown archive or
    ///     another type is a refusal before any write.
    /// </summary>
    internal static async Task<ArchiveMethodOutcome<T>> RunOnArchiveAsync<T>(ITenantClient tenantClient,
        string archiveRtId, Func<IArchiveMethodClient, Task<CkMethodResult<T>>> method)
    {
        ArchiveKind? kind;
        string? error;
        try
        {
            (kind, error) = await ResolveArchiveKindAsync(tenantClient, archiveRtId);
        }
        catch (Exception ex) when (IdentityMethodCall.IsMethodUnavailable(ex) || ex is QlQueryErrorException)
        {
            // A tenant without stream data (or without System.StreamData) has no archive query field: the same text as a
            // missing method; nothing was changed.
            return new ArchiveMethodOutcome<T>(false, default, UnavailableText(ex), (ex as CkMethodCallException)?.Code);
        }

        if (kind == null)
        {
            return new ArchiveMethodOutcome<T>(false, default, error, TargetNotFoundCode);
        }

        IArchiveMethodClient client = kind switch
        {
            ArchiveKind.Raw => new RtRawArchiveMethodClient(tenantClient),
            ArchiveKind.TimeRange => new RtTimeRangeArchiveMethodClient(tenantClient),
            _ => new RtRollupArchiveMethodClient(tenantClient)
        };
        return await RunAsync(() => method(client));
    }

    /// <summary>
    ///     The text of a method the asset repository does not offer: it names the System.StreamData version the tool needs;
    ///     nothing was changed and there is no REST fallback.
    /// </summary>
    internal static string UnavailableText(Exception exception)
    {
        var method = exception is CkMethodCallException ck ? $"the CK method {ck.MethodId}" : "this CK method";
        var noHandler = exception is CkMethodCallException { Code: HandlerNotAvailableCode };
        return $"The asset repository does not offer {method}. This tool needs a tenant with System.StreamData " +
               $"{MinimumSystemStreamDataVersion} (the stream-data archive methods)" +
               (noHandler
                   ? ", or no handler of the method is running right now (asset repository restarting?). "
                   : ". ") +
               "Nothing was changed; the tool does not fall back to the REST endpoint.";
    }

    /// <summary>
    ///     <c>null</c> when <paramref name="value" /> is a runtime id, otherwise the error text. The method documents declare
    ///     the ids as <c>OctoObjectId</c>; the check keeps a malformed id from reaching the server.
    /// </summary>
    internal static string? InvalidIdError(string name, string? value) =>
        !string.IsNullOrWhiteSpace(value) && OctoObjectId.TryParse(value, out _)
            ? null
            : $"{name} '{value}' is not a valid runtime id.";

    /// <summary>
    ///     The concrete archive type of <paramref name="archiveRtId" />, read with a GraphQL query of the tenant; <c>null</c>
    ///     with an error text when the archive is unknown or of another type (nothing was changed).
    /// </summary>
    internal static async Task<(ArchiveKind? Kind, string? Error)> ResolveArchiveKindAsync(ITenantClient tenantClient,
        string archiveRtId)
    {
        var result = await tenantClient.SendQueryAsync<ArchiveTypeDto>(new GraphQLRequest
        {
            Query = ArchiveCkTypeQuery,
            Variables = new { rtId = archiveRtId }
        });
        var ckTypeId = result?.Items?.FirstOrDefault()?.CkTypeId;
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return (null, $"Archive '{archiveRtId}' not found.");
        }

        var kind = ParseArchiveKind(ckTypeId);
        return kind == null
            ? (null, $"Archive '{archiveRtId}' is of type '{ckTypeId}', which is no RawArchive, TimeRangeArchive or RollupArchive.")
            : (kind, null);
    }

    /// <summary>
    ///     The archive kind of a CK type id such as <c>System.StreamData/RollupArchive</c> or
    ///     <c>System.StreamData/RawArchive-2</c> (the element version suffix is ignored, a tenant may hold a newer model);
    ///     <c>null</c> for another type.
    /// </summary>
    internal static ArchiveKind? ParseArchiveKind(string ckTypeId)
    {
        const string prefix = "System.StreamData/";
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
            "RawArchive" => ArchiveKind.Raw,
            "TimeRangeArchive" => ArchiveKind.TimeRange,
            "RollupArchive" => ArchiveKind.Rollup,
            _ => null
        };
    }

    /// <summary>
    ///     Reads the operation of a backfill until it names its queued job (progress stage <c>QUEUED</c>, parameter
    ///     <c>jobRtId</c>) or is final, at most <paramref name="timeout" />. A final operation without a job is the no-op of
    ///     a source archive without data; a job that ended Failed before the first read comes back with state <c>Failed</c>
    ///     and its reason. Neither job nor error after the timeout: the job id is not known yet (the backfill runs on).
    /// </summary>
    internal static async Task<QueuedJob> ReadQueuedJobAsync(ITenantClient tenantClient,
        CkOperationHandle<RtRecomputeJobInfoRecordDto?> handle, TimeSpan timeout, TimeProvider timeProvider)
    {
        string? jobRtId = null;
        var jobState = "Pending";
        using var named = new CancellationTokenSource();
        var progress = new SyncProgress(operation =>
        {
            if (operation.Progress?.Parameters.TryGetValue("jobRtId", out var id) == true && !string.IsNullOrEmpty(id))
            {
                jobRtId = id;
                // QUEUED is the stage that names the job; a later stage means the job already runs.
                jobState = string.Equals(operation.Progress.StageCode, "QUEUED", StringComparison.Ordinal)
                    ? "Pending"
                    : "Running";
                named.Cancel();
            }
        });
        CkOperation operation;
        try
        {
            operation = await new CkOperationsClient(tenantClient).WaitForCompletionAsync(handle, progress,
                new CkOperationWaitOptions { Timeout = timeout, TimeProvider = timeProvider }, named.Token);
        }
        catch (OperationCanceledException) when (jobRtId is not null)
        {
            return new QueuedJob(new RollupRecomputeJobInfoDto(jobRtId, jobState, null, null, null, null, null, null),
                false, null);
        }
        catch (CkOperationWaitTimeoutException)
        {
            return new QueuedJob(null, false, null);
        }
        catch (CkOperationQueryException ex)
        {
            return new QueuedJob(null, false, Safe(IdentityMethodCall.Describe(ex.Error)));
        }

        if (operation.State == CkOperationState.Failed)
        {
            var error = operation.Error;
            if (error?.Code == RecomputeFailedCode &&
                error.Parameters.TryGetValue("jobRtId", out var failedId) && !string.IsNullOrEmpty(failedId))
            {
                return new QueuedJob(new RollupRecomputeJobInfoDto(failedId, "Failed", null, null, null, null, null,
                    error.Parameters.GetValueOrDefault("reason")), false, null);
            }

            return new QueuedJob(null, false, error == null
                ? $"The backfill operation {handle.OperationId} failed without an error code."
                : Safe(IdentityMethodCall.Describe(error)));
        }

        var job = handle.ReadResult(operation);
        return job == null ? new QueuedJob(null, true, null) : new QueuedJob(ToJobInfo(job), false, null);
    }

    /// <summary>The job record of the method in the shape of the REST answer (<see cref="RollupRecomputeJobInfoDto" />).</summary>
    internal static RollupRecomputeJobInfoDto ToJobInfo(RtRecomputeJobInfoRecordDto job) =>
        new(job.JobRtId ?? string.Empty, job.State?.ToString() ?? "Unknown",
            (int?)job.RowsProcessed, (int?)job.WindowsProcessed, job.StartedAt, job.FinishedAt, (int?)job.DurationMs,
            job.ErrorReason)
        {
            LastProgressAt = job.LastProgressAt,
            ComputedColumnName = job.ComputedColumnName
        };

    /// <summary>The queued job of a backfill: the job (Pending, or the final one), the no-op, or an error text.</summary>
    internal sealed record QueuedJob(RollupRecomputeJobInfoDto? Job, bool NoData, string? Error);

    /// <summary>An <see cref="IProgress{T}" /> that reports synchronously, so the job id is known before the next read.</summary>
    private sealed class SyncProgress(Action<CkOperation> report) : IProgress<CkOperation>
    {
        public void Report(CkOperation value) => report(value);
    }

    private static string Safe(string text) => ErrorTextRedactor.Redact(text);

    /// <summary>The concrete types of <c>System.StreamData/Archive</c>.</summary>
    internal enum ArchiveKind
    {
        Raw,
        TimeRange,
        Rollup
    }

    /// <summary>The fields of the archive type query.</summary>
    internal sealed class ArchiveTypeDto
    {
        /// <summary>The runtime id of the archive.</summary>
        public string? RtId { get; set; }

        /// <summary>The CK type id the server answered (element-versioned, e.g. <c>System.StreamData/RollupArchive-1</c>).</summary>
        public string? CkTypeId { get; set; }
    }
}
