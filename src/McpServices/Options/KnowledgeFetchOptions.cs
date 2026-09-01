namespace Meshmakers.Octo.Backend.McpServices.Options;

/// <summary>
///     Server-side fetch policy for <c>AiKnowledgeSource</c> entities of kind <c>Url</c> (AB#5037).
///     <para>
///     The URL is tenant-writable data and the fetch runs from inside the cluster, so without a policy
///     it is a server-side request forgery primitive: cloud metadata endpoints, cluster-internal
///     services and exotic schemes would all be reachable, with the response body handed back to the
///     caller. Defaults here are deliberately restrictive; an operator opts specific internal sources
///     back in via <see cref="AllowedHosts" />.
///     </para>
///     Bound from the <c>KnowledgeFetch</c> configuration section (env: <c>OCTO_KNOWLEDGEFETCH__…</c>).
/// </summary>
public class KnowledgeFetchOptions
{
    /// <summary>
    ///     Hosts the fetch may reach even though they resolve into a blocked address range. Exact,
    ///     case-insensitive host match, or a leading-dot suffix match (<c>.internal.example.com</c>
    ///     matches <c>docs.internal.example.com</c> but not <c>internal.example.com</c>).
    ///     <para>
    ///     An entry here waives <b>only</b> the address-range check — scheme, size, timeout and the
    ///     redirect re-check still apply, and a redirect target must be allow-listed in its own right.
    ///     </para>
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = new List<string>();

    /// <summary>
    ///     When true, every host that passes the scheme check is fetched and the address-range check is
    ///     skipped entirely. The escape hatch for an operator who terminates egress in a proxy and does
    ///     not want a second policy here. Off by default — turning it on re-opens the SSRF surface.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>
    ///     Maximum number of bytes read from the response body. The body is truncated (and marked as
    ///     truncated in the rendered markdown) rather than failing the whole resource. Default 1 MiB.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>
    ///     Wall-clock budget for the whole fetch including every redirect hop. Default 10 s.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     Maximum number of redirects followed. Each hop is re-validated against the same policy, so a
    ///     redirect into a blocked range is refused rather than followed. Default 3; 0 disables
    ///     redirect following.
    /// </summary>
    public int MaxRedirects { get; set; } = 3;
}
