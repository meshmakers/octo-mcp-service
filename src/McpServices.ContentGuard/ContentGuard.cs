using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.McpServices.ContentGuard;

/// <summary>
///     Refuses writes whose text looks like a credential or holds a secret value of the tenant (AB#8475, concept
///     tenant-builder-agent §3.9). Knowledge page writes (AB#8484) call it before anything is stored; the memory writes
///     of the agent runtime (AB#8473) are the second user.
/// </summary>
/// <remarks>
///     A refusal is logged at Warning with tenant, caller, operation, detectors and field names - never with the
///     content. Once ActionRecords exist (AB#8467) the caller records the refusal there with status Refused.
/// </remarks>
public sealed class ContentGuard(ILogger<ContentGuard> logger) : IContentGuard
{
    /// <inheritdoc />
    public async Task<ContentGuardResult> CheckAsync(ContentGuardContext context,
        IReadOnlyCollection<ContentGuardField> fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);

        var findings = new List<ContentGuardFinding>();
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field.Text))
            {
                continue;
            }

            if (ContentDetectors.ContainsAnthropicKey(field.Text))
            {
                findings.Add(new ContentGuardFinding(ContentGuardDetector.AnthropicKey, field.Name));
            }

            if (ContentDetectors.ContainsPemBlock(field.Text))
            {
                findings.Add(new ContentGuardFinding(ContentGuardDetector.PemBlock, field.Name));
            }

            if (ContentDetectors.ContainsJwt(field.Text))
            {
                findings.Add(new ContentGuardFinding(ContentGuardDetector.Jwt, field.Name));
            }

            if (ContentDetectors.ContainsHighEntropyToken(field.Text))
            {
                findings.Add(new ContentGuardFinding(ContentGuardDetector.HighEntropyToken, field.Name));
            }
        }

        if (context.SecretFingerprints != null && fields.Any(f => !string.IsNullOrEmpty(f.Text)))
        {
            IReadOnlySet<string> fingerprints;
            try
            {
                fingerprints = await context.SecretFingerprints(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail closed: a write whose secrets cannot be compared is refused rather than stored unchecked.
                logger.LogWarning(ex,
                    "Content guard could not read the secret fingerprints of tenant {TenantId}; refusing {Operation} by {Caller}",
                    context.TenantId, context.Operation, context.Caller);
                findings.AddRange(fields.Where(f => !string.IsNullOrEmpty(f.Text))
                    .Select(f => new ContentGuardFinding(ContentGuardDetector.TenantSecret, f.Name)));
                fingerprints = new HashSet<string>();
            }

            foreach (var field in fields)
            {
                if (!string.IsNullOrEmpty(field.Text) && ContentDetectors.ContainsSecretValue(field.Text, fingerprints))
                {
                    findings.Add(new ContentGuardFinding(ContentGuardDetector.TenantSecret, field.Name));
                }
            }
        }

        var result = ContentGuardResult.FromFindings(findings);
        if (result.IsRejected)
        {
            logger.LogWarning(
                "Content guard refused {Operation} in tenant {TenantId} by {Caller}: {Detectors} in {Fields}",
                context.Operation, context.TenantId, context.Caller,
                string.Join(",", result.Findings.Select(f => f.Detector).Distinct()),
                string.Join(",", result.Findings.Select(f => f.FieldName).Distinct()));
        }

        return result;
    }
}
