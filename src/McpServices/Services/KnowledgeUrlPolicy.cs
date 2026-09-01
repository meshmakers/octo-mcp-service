using System.Net;
using System.Net.Sockets;
using Meshmakers.Octo.Backend.McpServices.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Resolves host names to IP addresses for <see cref="KnowledgeUrlPolicy" />. Extracted so the
///     policy can be tested without a real DNS server — production uses <see cref="SystemDnsResolver" />.
/// </summary>
public interface IHostAddressResolver
{
    /// <summary>
    ///     Resolves every address a host name maps to.
    /// </summary>
    /// <param name="host">Host name or IP literal from the URL's authority.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
///     Production <see cref="IHostAddressResolver" /> backed by <see cref="Dns" />.
/// </summary>
public sealed class SystemDnsResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}

/// <summary>
///     Outcome of a <c>KnowledgeUrlPolicy.ValidateAsync</c> call: either the URI may be fetched, or
///     <see cref="Error" /> explains why not. Exactly one of the two is set.
/// </summary>
/// <param name="Uri">The validated absolute URI; null when <paramref name="Error" /> is set.</param>
/// <param name="Error">Refusal reason rendered into the resource markdown, or null on success.</param>
internal sealed record KnowledgeUrlDecision(Uri? Uri, string? Error);

/// <summary>
///     SSRF policy for server-side knowledge-source fetches (AB#5037).
///     <para>
///     Four rules, in this order: the URL must be absolute and <c>http</c>/<c>https</c>; the host must
///     resolve; <b>every</b> resolved address must be publicly routable (loopback, private, link-local,
///     carrier-grade NAT, unique-local, multicast and the cloud metadata endpoints are blocked); and an
///     operator-configured allow-list may waive the address check for named internal hosts.
///     </para>
///     <para>
///     <b>The address check runs after DNS resolution on purpose.</b> Checking the literal host would be
///     defeated by any name that resolves into a blocked range — including a name the tenant controls
///     that points at <c>169.254.169.254</c>. All addresses are checked, not just the first, so a
///     multi-record name cannot smuggle one blocked address past the gate.
///     </para>
///     <para>
///     The same validation is applied to every redirect target by <see cref="KnowledgeUrlFetcher" />, so
///     a public host cannot bounce the request into the cluster.
///     </para>
/// </summary>
internal sealed class KnowledgeUrlPolicy(IHostAddressResolver hostAddressResolver)
{
    /// <summary>
    ///     IPv4 cloud instance-metadata endpoint (AWS / Azure / GCP / OpenStack all use 169.254.169.254).
    ///     Already inside the link-local range, listed separately so the refusal names it.
    /// </summary>
    private static readonly IPAddress Ipv4MetadataEndpoint = IPAddress.Parse("169.254.169.254");

    /// <summary>
    ///     IPv6 instance-metadata endpoint (AWS <c>fd00:ec2::254</c>). Inside the unique-local range.
    /// </summary>
    private static readonly IPAddress Ipv6MetadataEndpoint = IPAddress.Parse("fd00:ec2::254");

    /// <summary>
    ///     Validates one URL against <paramref name="options" />.
    /// </summary>
    /// <param name="url">The stored knowledge-source URL, or a redirect target.</param>
    /// <param name="options">Operator policy.</param>
    /// <param name="cancellationToken">Cancels the DNS lookup.</param>
    public async Task<KnowledgeUrlDecision> ValidateAsync(
        string url, KnowledgeFetchOptions options, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new KnowledgeUrlDecision(null, $"'{url}' is not an absolute URL.");
        }

        return await ValidateAsync(uri, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Validates one already-parsed absolute URI against <paramref name="options" />.
    /// </summary>
    /// <param name="uri">The URI to fetch.</param>
    /// <param name="options">Operator policy.</param>
    /// <param name="cancellationToken">Cancels the DNS lookup.</param>
    public async Task<KnowledgeUrlDecision> ValidateAsync(
        Uri uri, KnowledgeFetchOptions options, CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new KnowledgeUrlDecision(null,
                $"URL scheme '{uri.Scheme}' is not allowed — only http and https knowledge sources are fetched.");
        }

        if (options.AllowPrivateNetworks || IsAllowListed(uri.Host, options))
        {
            return new KnowledgeUrlDecision(uri, null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)
                ? [literal]
                : await hostAddressResolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // Fail closed: a host we cannot resolve is a host we must not connect to.
            return new KnowledgeUrlDecision(null, $"Host '{uri.Host}' could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return new KnowledgeUrlDecision(null, $"Host '{uri.Host}' could not be resolved.");
        }

        // Every address, not just the first: a name with one public and one internal record must not
        // pass, because which record the connection actually uses is not ours to decide.
        foreach (var address in addresses)
        {
            if (IsBlocked(address))
            {
                return new KnowledgeUrlDecision(null,
                    $"Host '{uri.Host}' resolves to the blocked address {address} "
                    + "(loopback, private, link-local or metadata range). "
                    + "Add the host to KnowledgeFetch:AllowedHosts to allow it deliberately.");
            }
        }

        return new KnowledgeUrlDecision(uri, null);
    }

    /// <summary>
    ///     True when <paramref name="host" /> matches an <see cref="KnowledgeFetchOptions.AllowedHosts" />
    ///     entry: exact case-insensitive match, or suffix match for an entry starting with a dot.
    /// </summary>
    /// <param name="host">Host from the URL authority.</param>
    /// <param name="options">Operator policy.</param>
    private static bool IsAllowListed(string host, KnowledgeFetchOptions options)
    {
        foreach (var entry in options.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (trimmed.StartsWith('.'))
            {
                if (host.EndsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True for any address that must not be reached from the cluster: loopback, IPv4 private
    ///     (10/8, 172.16/12, 192.168/16), carrier-grade NAT (100.64/10), link-local (169.254/16 incl. the
    ///     cloud metadata endpoint), "this network" (0/8), multicast/broadcast, IPv6 loopback,
    ///     unique-local (fc00::/7), link-local (fe80::/10), and IPv4-mapped/compatible IPv6 forms of any
    ///     of the above.
    /// </summary>
    /// <param name="address">A resolved address of the target host.</param>
    internal static bool IsBlocked(IPAddress address)
    {
        if (address.Equals(Ipv4MetadataEndpoint) || address.Equals(Ipv6MetadataEndpoint))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6 || address.IsIPv4Compatible())
            {
                // Unwrap ::ffff:10.0.0.1 and friends, otherwise the IPv4 rules below never apply.
                return IsBlocked(address.MapToIPv4());
            }

            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            // Neither IPv4 nor IPv6 — nothing we know how to classify, so refuse.
            return true;
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true,                                       // 0.0.0.0/8 "this network"
            10 => true,                                      // RFC 1918
            127 => true,                                     // loopback (covered above, kept explicit)
            100 when octets[1] >= 64 && octets[1] <= 127 => true, // RFC 6598 carrier-grade NAT
            169 when octets[1] == 254 => true,               // link-local incl. metadata
            172 when octets[1] >= 16 && octets[1] <= 31 => true, // RFC 1918
            192 when octets[1] == 168 => true,               // RFC 1918
            >= 224 => true,                                  // multicast + reserved + broadcast
            _ => false
        };
    }
}

/// <summary>
///     Extension helpers for <see cref="IPAddress" /> classification.
/// </summary>
internal static class IpAddressExtensions
{
    /// <summary>
    ///     True for the deprecated IPv4-compatible IPv6 form <c>::a.b.c.d</c> (all-zero prefix), which
    ///     <see cref="IPAddress.IsIPv4MappedToIPv6" /> does not cover but which still routes to the IPv4
    ///     address it embeds.
    /// </summary>
    /// <param name="address">The address to test.</param>
    public static bool IsIPv4Compatible(this IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        // ::0 and ::1 are the unspecified / loopback addresses, handled by the IPv6 rules.
        return bytes[12] != 0 || bytes[13] != 0 || bytes[14] != 0;
    }
}
