using System.Net;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Result of a guarded knowledge-source fetch. Exactly one of <see cref="Body" /> /
///     <see cref="Error" /> is set.
/// </summary>
/// <param name="Body">Response body, possibly truncated; null when <paramref name="Error" /> is set.</param>
/// <param name="Truncated">True when the body hit <see cref="KnowledgeFetchOptions.MaxResponseBytes" />.</param>
/// <param name="Error">Refusal / failure reason rendered into the resource markdown, or null on success.</param>
internal sealed record KnowledgeFetchResult(string? Body, bool Truncated, string? Error);

/// <summary>
///     Fetches an <c>AiKnowledgeSource</c> of kind <c>Url</c> under the AB#5037 SSRF policy.
/// </summary>
internal interface IKnowledgeUrlFetcher
{
    /// <summary>
    ///     Validates and fetches <paramref name="url" />, following redirects manually so every hop is
    ///     re-validated. Never throws — failures come back as
    ///     <see cref="KnowledgeFetchResult.Error" />.
    /// </summary>
    /// <param name="url">The stored knowledge-source URL (tenant-writable data).</param>
    /// <param name="cancellationToken">Token from the MCP transport.</param>
    Task<KnowledgeFetchResult> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
///     Default <see cref="IKnowledgeUrlFetcher" />: policy check, bounded body, bounded wall clock,
///     manually followed redirects (AB#5037).
///     <para>
///     Redirects are followed by hand rather than by <c>HttpClientHandler</c> because auto-redirect
///     would connect to the target before anything could inspect it — the exact hole the policy exists
///     to close. Each <c>Location</c> is run through <see cref="KnowledgeUrlPolicy" /> again, so a
///     public host cannot bounce the request into the cluster or at a metadata endpoint.
///     </para>
///     <para>
///     The body is read from the stream with a byte budget instead of
///     <c>ReadAsStringAsync</c>: a hostile or merely huge source must not be able to spend the pod's
///     memory or the AI client's token budget. Hitting the budget truncates and says so; it is not an
///     error, because a partial CLAUDE.md fragment is still useful.
///     </para>
/// </summary>
internal sealed class KnowledgeUrlFetcher(
    IHttpClientFactory httpClientFactory,
    KnowledgeUrlPolicy policy,
    IOptions<KnowledgeFetchOptions> options)
    : IKnowledgeUrlFetcher
{
    /// <inheritdoc />
    public async Task<KnowledgeFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var opts = options.Value;

        var decision = await policy.ValidateAsync(url, opts, cancellationToken).ConfigureAwait(false);
        if (decision.Error != null)
        {
            return new KnowledgeFetchResult(null, false, decision.Error);
        }

        // One budget for the whole exchange including redirects — a redirect chain must not be able to
        // multiply the configured timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.TimeoutSeconds)));

        try
        {
            return await FetchWithRedirectsAsync(decision.Uri!, opts, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KnowledgeFetchResult(null, false,
                $"the fetch exceeded the {opts.TimeoutSeconds}s knowledge-fetch timeout");
        }
        catch (Exception ex)
        {
            return new KnowledgeFetchResult(null, false, ex.Message);
        }
    }

    private async Task<KnowledgeFetchResult> FetchWithRedirectsAsync(
        Uri uri, KnowledgeFetchOptions opts, CancellationToken ct)
    {
        // Named client "knowledge-fetch" lets ops set proxy / header policy in appsettings.json without
        // recompiling. Timeout and redirect handling are owned here, not by the client.
        var http = httpClientFactory.CreateClient("knowledge-fetch");

        var maxRedirects = Math.Max(0, opts.MaxRedirects);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    return new KnowledgeFetchResult(null, false,
                        $"HTTP {(int)response.StatusCode} without a Location header");
                }

                if (hop >= maxRedirects)
                {
                    return new KnowledgeFetchResult(null, false,
                        $"more than {maxRedirects} redirect(s) followed");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                var decision = await policy.ValidateAsync(next, opts, ct).ConfigureAwait(false);
                if (decision.Error != null)
                {
                    return new KnowledgeFetchResult(null, false,
                        $"redirect to '{next}' refused — {decision.Error}");
                }

                uri = decision.Uri!;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new KnowledgeFetchResult(null, false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return await ReadBoundedBodyAsync(response, opts, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads at most <see cref="KnowledgeFetchOptions.MaxResponseBytes" /> from the response stream.
    ///     A declared <c>Content-Length</c> over the budget is refused up front so an oversized body is
    ///     not streamed at all; an undeclared one is cut off mid-stream.
    /// </summary>
    private static async Task<KnowledgeFetchResult> ReadBoundedBodyAsync(
        HttpResponseMessage response, KnowledgeFetchOptions opts, CancellationToken ct)
    {
        var maxBytes = Math.Max(1, opts.MaxResponseBytes);

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return new KnowledgeFetchResult(null, false,
                $"the response declares {declared} bytes, over the {maxBytes}-byte knowledge-fetch limit");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[8192];
        using var body = new MemoryStream();
        var truncated = false;

        while (body.Length < maxBytes)
        {
            var wanted = (int)Math.Min(buffer.Length, maxBytes - body.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            body.Write(buffer, 0, read);
        }

        if (body.Length >= maxBytes)
        {
            // One more byte decides "exactly at the limit" from "cut off".
            truncated = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) > 0;
        }

        return new KnowledgeFetchResult(Encoding.UTF8.GetString(body.ToArray()), truncated, null);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
