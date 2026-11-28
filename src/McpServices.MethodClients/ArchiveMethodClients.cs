using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;

namespace Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.StreamData.v1;

/// <summary>
///     The lifecycle and column methods of the abstract <c>System.StreamData/Archive</c> the archive tools call. The generator
///     emits them on the client of every concrete archive type with the same signatures; the partial declarations below make
///     each client implement this interface, so a tool picks the client of the archive's concrete type once.
/// </summary>
public interface IArchiveMethodClient
{
    /// <summary><c>Archive.Activate-1</c>.</summary>
    Task<CkMethodResult<RtArchiveTransitionResultRecordDto?>> ActivateAsync(string rtId, string? idempotencyKey = null);

    /// <summary><c>Archive.Disable-1</c>.</summary>
    Task<CkMethodResult<RtArchiveTransitionResultRecordDto?>> DisableAsync(string rtId, string? idempotencyKey = null);

    /// <summary><c>Archive.Enable-1</c>.</summary>
    Task<CkMethodResult<RtArchiveTransitionResultRecordDto?>> EnableAsync(string rtId, string? idempotencyKey = null);

    /// <summary><c>Archive.RetryActivation-1</c>.</summary>
    Task<CkMethodResult<RtArchiveTransitionResultRecordDto?>> RetryActivationAsync(string rtId,
        string? idempotencyKey = null);

    /// <summary><c>Archive.DeleteArchive-1</c>.</summary>
    Task<CkMethodResult<bool?>> DeleteArchiveAsync(string rtId, string? idempotencyKey = null);

    /// <summary><c>Archive.AddColumn-1</c>.</summary>
    Task<CkMethodResult<RtArchiveTransitionResultRecordDto?>> AddColumnAsync(string rtId, string path,
        bool? indexed = null, string? idempotencyKey = null);
}

/// <summary>The client of raw archives implements the shared Archive methods.</summary>
public partial class RtRawArchiveMethodClient : IArchiveMethodClient;

/// <summary>The client of time-range archives implements the shared Archive methods.</summary>
public partial class RtTimeRangeArchiveMethodClient : IArchiveMethodClient;

/// <summary>The client of rollup archives implements the shared Archive methods.</summary>
public partial class RtRollupArchiveMethodClient : IArchiveMethodClient;
