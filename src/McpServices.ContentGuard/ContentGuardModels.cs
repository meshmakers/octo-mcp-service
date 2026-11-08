namespace Meshmakers.Octo.Backend.McpServices.ContentGuard;

/// <summary>
///     What the content guard found. The names are part of the refusal and of the log line, so keep them stable.
/// </summary>
public enum ContentGuardDetector
{
    /// <summary>An Anthropic API key: <c>sk-ant-</c> followed by a key type and a dash, e.g. <c>sk-ant-api03-</c>.</summary>
    AnthropicKey,

    /// <summary>A PEM block (<c>-----BEGIN ...-----</c> with a body and its <c>-----END ...-----</c>).</summary>
    PemBlock,

    /// <summary>A JSON Web Token (three base64url parts, header and payload starting with <c>eyJ</c>).</summary>
    Jwt,

    /// <summary>A long random-looking token: mixed letters and digits with a high character entropy.</summary>
    HighEntropyToken,

    /// <summary>A value equal to a Secret attribute value of the tenant.</summary>
    TenantSecret
}

/// <summary>One text field of a write the guard checks, e.g. a page title or a section body.</summary>
/// <param name="Name">Field name used in the refusal and the log line (never the content).</param>
/// <param name="Text">The text to check; <c>null</c> or empty is skipped.</param>
public sealed record ContentGuardField(string Name, string? Text);

/// <summary>Who writes what, for the log line of a refusal, and where the tenant's secret values come from.</summary>
/// <param name="TenantId">Tenant the write goes to.</param>
/// <param name="Caller">User name or client id of the caller.</param>
/// <param name="Operation">The write, e.g. <c>knowledge_upsert_page</c>.</param>
/// <param name="SecretFingerprints">
///     Supplies the SHA-256 fingerprints (<see cref="SecretFingerprint.Compute" />) of the Secret attribute values the
///     caller could reveal in the tenant, so the guard can refuse a text holding one without keeping a plaintext. The
///     host reads them with the caller's own rights. <c>null</c> switches detector
///     <see cref="ContentGuardDetector.TenantSecret" /> off (hosts without a key ring).
/// </param>
public sealed record ContentGuardContext(string TenantId, string Caller, string Operation,
    Func<CancellationToken, ValueTask<IReadOnlySet<string>>>? SecretFingerprints = null);

/// <summary>One finding: which detector fired in which field.</summary>
/// <param name="Detector">The detector.</param>
/// <param name="FieldName">The field the detector fired in.</param>
public sealed record ContentGuardFinding(ContentGuardDetector Detector, string FieldName);

/// <summary>Result of a content check.</summary>
public sealed class ContentGuardResult
{
    /// <summary>Error code callers return for a refused write.</summary>
    public const string ErrorCode = "ContentRejected";

    private ContentGuardResult(IReadOnlyList<ContentGuardFinding> findings)
    {
        Findings = findings;
    }

    /// <summary>A result without findings.</summary>
    public static ContentGuardResult Accepted { get; } = new([]);

    /// <summary>The findings; empty when the content is accepted.</summary>
    public IReadOnlyList<ContentGuardFinding> Findings { get; }

    /// <summary>True when at least one detector fired.</summary>
    public bool IsRejected => Findings.Count > 0;

    /// <summary>
    ///     The refusal text for the caller: error code, detectors and fields. Never contains the content.
    /// </summary>
    public string Message => IsRejected
        ? $"{ErrorCode}: the content looks like a credential or a secret value and was not written " +
          $"({string.Join(", ", Findings.Select(f => $"{f.Detector} in {f.FieldName}"))}). " +
          "Remove it; knowledge pages must never hold credentials."
        : string.Empty;

    /// <summary>Creates a result from findings.</summary>
    /// <param name="findings">The findings (duplicates are removed).</param>
    public static ContentGuardResult FromFindings(IEnumerable<ContentGuardFinding> findings)
    {
        var distinct = findings.Distinct().ToList();
        return distinct.Count == 0 ? Accepted : new ContentGuardResult(distinct);
    }
}

/// <summary>Checks the text of a write before it is stored.</summary>
public interface IContentGuard
{
    /// <summary>Checks the fields and logs a refusal (tenant, caller, operation, detectors, fields; never the content).</summary>
    /// <param name="context">Who writes what.</param>
    /// <param name="fields">The text fields of the write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ContentGuardResult> CheckAsync(ContentGuardContext context, IReadOnlyCollection<ContentGuardField> fields,
        CancellationToken cancellationToken = default);
}
