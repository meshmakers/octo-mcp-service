using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;

namespace Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;

/// <summary>A workload and its current sizing, as read from the tenant.</summary>
/// <param name="RtId">Runtime id of the workload.</param>
/// <param name="Name">Name of the workload.</param>
/// <param name="Kind"><c>Adapter</c> or <c>Application</c>.</param>
/// <param name="Settings">The four sizing values and the last deployment warning.</param>
internal sealed record WorkloadResourcesState(string RtId, string Name, string Kind, WorkloadResourceSettings Settings);

/// <summary>
///     Reads and writes the typed sizing attributes of an adapter or application through the tenant's GraphQL
///     endpoint (AB#7429). The communication controller has no endpoint for them: they are runtime state on the
///     <c>System.Communication</c> entity (4.10.0), written like any other entity. Documents are plain text, so the
///     CLI builds without the generated GraphQL types of the 4.10.0 model; a tenant that still runs an older model
///     answers with a clear message.
/// </summary>
internal sealed class WorkloadResourcesAccess(IAssetServicesClient assetClient)
{
    private const string Fields = "rtId name cpuRequest cpuLimit memoryRequest memoryLimit lastDeploymentWarning";

    private const string ReadQuery =
        "query workloadResources($rtId: OctoObjectId!) { runtime { " +
        "adapters: systemCommunicationAdapter(rtId: $rtId) { items { " + Fields + " } } " +
        "applications: systemCommunicationApplication(rtId: $rtId) { items { " + Fields + " } } } }";

    internal const string AdapterMutation =
        "mutation updateWorkloadResources($entity: SystemCommunicationAdapterInputUpdate!) { runtime { " +
        "systemCommunicationAdapters { update(entities: [$entity]) { " + Fields + " } } } }";

    internal const string ApplicationMutation =
        "mutation updateWorkloadResources($entity: SystemCommunicationApplicationInputUpdate!) { runtime { " +
        "systemCommunicationApplications { update(entities: [$entity]) { " + Fields + " } } } }";

    private static readonly string[] CamelCaseNames =
        ["cpuRequest", "cpuLimit", "memoryRequest", "memoryLimit"];

    /// <summary>Finds the adapter or application with <paramref name="rtId" />; <c>null</c> when there is none.</summary>
    /// <exception cref="WorkloadResourcesException">The tenant answered with GraphQL errors.</exception>
    public async Task<WorkloadResourcesState?> FindAsync(string tenantId, string rtId)
    {
        var variables = new JsonObject { ["rtId"] = rtId }.ToJsonString();
        var response = await assetClient.SendGraphQlAsync(tenantId, ReadQuery, variables);
        var root = ParseOrThrow(response, tenantId);
        var runtime = root["data"]?["runtime"];

        var adapter = FirstItem(runtime?["adapters"]);
        if (adapter != null)
        {
            return ToState(adapter, "Adapter");
        }

        var application = FirstItem(runtime?["applications"]);
        return application != null ? ToState(application, "Application") : null;
    }

    /// <summary>
    ///     Writes the given changes (attribute name to new value; a <c>null</c> value clears the attribute) and returns
    ///     the stored state.
    /// </summary>
    /// <exception cref="WorkloadResourcesException">The tenant refused the write.</exception>
    public async Task<WorkloadResourcesState> UpdateAsync(string tenantId, WorkloadResourcesState workload,
        IReadOnlyDictionary<string, string?> changes)
    {
        var item = new JsonObject();
        foreach (var (name, value) in changes)
        {
            item[char.ToLowerInvariant(name[0]) + name[1..]] = value;
        }

        var variables = new JsonObject
        {
            ["entity"] = new JsonObject { ["rtId"] = workload.RtId, ["item"] = item }
        }.ToJsonString();

        var mutation = workload.Kind == "Adapter" ? AdapterMutation : ApplicationMutation;
        var response = await assetClient.SendGraphQlAsync(tenantId, mutation, variables);
        var root = ParseOrThrow(response, tenantId);

        var field = workload.Kind == "Adapter" ? "systemCommunicationAdapters" : "systemCommunicationApplications";
        var updated = root["data"]?["runtime"]?[field]?["update"] as JsonArray;
        var first = updated is { Count: > 0 } ? updated[0] as JsonObject : null;
        return first != null ? ToState(first, workload.Kind) : workload;
    }

    private static JsonObject ParseOrThrow(GraphQlRawResponse response, string tenantId)
    {
        JsonObject? root = null;
        try
        {
            root = JsonNode.Parse(response.Content) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // handled below
        }

        if (root == null)
        {
            throw new WorkloadResourcesException(
                $"The GraphQL endpoint of tenant '{tenantId}' answered with HTTP {(int)response.StatusCode} and no JSON.");
        }

        if (response.HasErrors)
        {
            var messages = (root["errors"] as JsonArray ?? [])
                .Select(e => e?["message"]?.GetValue<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .ToList();
            var text = string.Join("; ", messages);
            if (CamelCaseNames.Any(n => text.Contains(n, StringComparison.Ordinal)) ||
                text.Contains("InputUpdate", StringComparison.Ordinal))
            {
                throw new WorkloadResourcesException(
                    $"Tenant '{tenantId}' does not know the CPU/memory attributes of adapters and applications. " +
                    "They come with System.Communication 4.10.0; update the tenant's model first. " +
                    $"GraphQL said: {text}");
            }

            throw new WorkloadResourcesException($"The GraphQL request to tenant '{tenantId}' failed: {text}");
        }

        return root;
    }

    private static JsonObject? FirstItem(JsonNode? connection) =>
        connection?["items"] is JsonArray { Count: > 0 } items ? items[0] as JsonObject : null;

    private static WorkloadResourcesState ToState(JsonObject item, string kind) =>
        new(item["rtId"]?.GetValue<string>() ?? string.Empty,
            item["name"]?.GetValue<string>() ?? string.Empty,
            kind,
            new WorkloadResourceSettings(
                Text(item, "cpuRequest"), Text(item, "cpuLimit"), Text(item, "memoryRequest"),
                Text(item, "memoryLimit"), Text(item, "lastDeploymentWarning")));

    private static string? Text(JsonObject item, string name)
    {
        var value = item[name]?.GetValue<string>();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>The tenant could not read or write the sizing attributes; the message is meant for the caller.</summary>
internal sealed class WorkloadResourcesException(string message) : Exception(message);
