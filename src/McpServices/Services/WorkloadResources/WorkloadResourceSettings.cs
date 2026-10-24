namespace Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;

/// <summary>
///     The four typed sizing values of a deployable workload (<c>System.Communication</c> 4.10.0, AB#7426) plus the
///     read-only deployment warning. An empty value means "not set"; the chart default applies.
/// </summary>
internal sealed record WorkloadResourceSettings(
    string? CpuRequest,
    string? CpuLimit,
    string? MemoryRequest,
    string? MemoryLimit,
    string? LastDeploymentWarning = null)
{
    /// <summary>Attribute names as they appear in messages, in display order.</summary>
    public static readonly string[] AttributeNames = ["CpuRequest", "CpuLimit", "MemoryRequest", "MemoryLimit"];

    /// <summary>The value of the attribute named <paramref name="attributeName" />.</summary>
    public string? Get(string attributeName) => attributeName switch
    {
        "CpuRequest" => CpuRequest,
        "CpuLimit" => CpuLimit,
        "MemoryRequest" => MemoryRequest,
        "MemoryLimit" => MemoryLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(attributeName), attributeName, null)
    };

    /// <summary>A copy with the attribute named <paramref name="attributeName" /> set to <paramref name="value" />.</summary>
    public WorkloadResourceSettings With(string attributeName, string? value) => attributeName switch
    {
        "CpuRequest" => this with { CpuRequest = value },
        "CpuLimit" => this with { CpuLimit = value },
        "MemoryRequest" => this with { MemoryRequest = value },
        "MemoryLimit" => this with { MemoryLimit = value },
        _ => throw new ArgumentOutOfRangeException(nameof(attributeName), attributeName, null)
    };

    /// <summary>
    ///     Validates the values the way the communication controller does at deploy time (AB#7427): every non-empty
    ///     value is a Kubernetes quantity, and a request does not exceed its limit (compared as parsed quantities, so
    ///     <c>1024Mi</c> equals <c>1Gi</c>).
    /// </summary>
    /// <returns>One message per violation, each naming the attribute; empty when the values are valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var parsed = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var name in AttributeNames)
        {
            var value = Get(name);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (KubernetesQuantity.TryParse(value, out var number))
            {
                parsed[name] = number;
            }
            else
            {
                errors.Add($"{name} '{value}' is not a valid Kubernetes quantity");
            }
        }

        CheckRequestAgainstLimit(errors, parsed, "Cpu");
        CheckRequestAgainstLimit(errors, parsed, "Memory");
        return errors;
    }

    private void CheckRequestAgainstLimit(List<string> errors, Dictionary<string, decimal> parsed, string resource)
    {
        var requestName = resource + "Request";
        var limitName = resource + "Limit";
        if (parsed.TryGetValue(requestName, out var request) && parsed.TryGetValue(limitName, out var limit) &&
            request > limit)
        {
            errors.Add($"{requestName} {Get(requestName)} exceeds {limitName} {Get(limitName)}");
        }
    }
}
