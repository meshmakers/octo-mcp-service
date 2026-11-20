using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Identity wave 2: maps the result records of the CK methods to the REST DTOs the tools returned before, so a tool's
///     response fields stay the same now that the method does the write.
/// </summary>
internal static class IdentityMethodResults
{
    /// <summary><c>Client.ApplyOverlayUris-1</c> result as <see cref="ApplyOverlayUrisResultDto" />.</summary>
    internal static ApplyOverlayUrisResultDto ToDto(RtClientOverlayUrisResultRecordDto? record, string clientId,
        string overlayName) => new()
    {
        OverlayName = record?.OverlayName ?? overlayName,
        ClientId = record?.ClientId ?? clientId,
        RedirectUris = Count(record?.RedirectUrisAdded, record?.RedirectUrisSkipped),
        PostLogoutRedirectUris = Count(record?.PostLogoutRedirectUrisAdded, record?.PostLogoutRedirectUrisSkipped),
        AllowedCorsOrigins = Count(record?.AllowedCorsOriginsAdded, record?.AllowedCorsOriginsSkipped)
    };

    /// <summary><c>Client.CleanOverlayEntries-1</c> result as <see cref="CleanOverlayEntriesResultDto" />.</summary>
    internal static CleanOverlayEntriesResultDto ToDto(RtClientOverlayCleanResultRecordDto? record,
        string? overlayName) => new()
    {
        OverlayName = record?.OverlayName ?? overlayName,
        ClientsAffected = ToInt(record?.ClientsAffected),
        TotalEntriesRemoved = ToInt(record?.TotalEntriesRemoved),
        ClientResults = (record?.ClientResults ?? [])
            .Select(entry => new CleanOverlayEntriesClientResultDto
            {
                ClientId = entry.ClientId ?? string.Empty,
                RedirectUrisRemoved = ToInt(entry.RedirectUrisRemoved),
                PostLogoutRedirectUrisRemoved = ToInt(entry.PostLogoutRedirectUrisRemoved),
                AllowedCorsOriginsRemoved = ToInt(entry.AllowedCorsOriginsRemoved)
            })
            .ToList()
    };

    /// <summary><c>Client.ProvisionMirrors-1</c> result as <see cref="ClientMirrorBackfillResponseDto" />.</summary>
    internal static ClientMirrorBackfillResponseDto ToDto(RtClientMirrorBackfillResultRecordDto? record) =>
        new(ToInt(record?.ChildTenantsConsidered), ToInt(record?.NewlyProvisioned), ToInt(record?.AlreadyPresent));

    /// <summary><c>Client.ProvisionMirrorInTenant-1</c> result as <see cref="ClientMirrorProvisionResponseDto" />.</summary>
    internal static ClientMirrorProvisionResponseDto ToDto(RtClientMirrorProvisionResultRecordDto? record) =>
        new(ToInt(record?.FlaggedClientsConsidered), ToInt(record?.NewlyProvisioned), ToInt(record?.AlreadyPresent));

    private static ApplyOverlayUrisListCountDto Count(long? added, long? skipped) =>
        new() { Added = ToInt(added), SkippedDuplicate = ToInt(skipped) };

    private static int ToInt(long? value) => value is null ? 0 : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
}
