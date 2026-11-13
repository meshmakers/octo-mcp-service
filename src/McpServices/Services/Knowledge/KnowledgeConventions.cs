using System.Collections.Concurrent;
using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.McpServices.Services.Knowledge;

/// <summary>The answering conventions a <c>knowledge_search</c> response carries, and where they come from.</summary>
/// <param name="Text">The conventions text, at most <see cref="KnowledgeConventions.MaxLength" /> characters.</param>
/// <param name="Source">
///     <c>knowledge-schema@&lt;version&gt;</c> when the text is the section of the tenant's schema page,
///     <see cref="KnowledgeConventions.DefaultSource" /> for the built-in default.
/// </param>
internal sealed record KnowledgeAnsweringConventions(string Text, string Source);

/// <summary>
///     Caches the bounded "Answering questions" text per tenant and schema page version (AB#8712). The key holds the page
///     rtId and its current version, so a new version of the schema page is a cache miss and is read again; the page
///     itself is always looked up with the caller's session, so the cache never hands the text to a caller who cannot
///     read the page.
/// </summary>
internal sealed class KnowledgeConventionsCache
{
    private readonly ConcurrentDictionary<string, (string PageRtId, int Version, string Text)> _entries =
        new(StringComparer.Ordinal);

    internal bool TryGet(string tenantId, string pageRtId, int version, out string text)
    {
        if (_entries.TryGetValue(tenantId, out var entry) && entry.PageRtId == pageRtId && entry.Version == version)
        {
            text = entry.Text;
            return true;
        }

        text = string.Empty;
        return false;
    }

    internal void Set(string tenantId, string pageRtId, int version, string text) =>
        _entries[tenantId] = (pageRtId, version, text);
}

/// <summary>
///     The answering conventions of a tenant's knowledge base (AB#8712): the "Answering questions" section of the schema
///     page <c>knowledge-schema</c>, bounded to <see cref="MaxLength" /> characters, or a built-in default when the tenant
///     has no schema page, the caller cannot read it, neither the page nor (while the page is Draft) an earlier version is
///     Verified, or the text has no such section. <c>knowledge_search</c> returns it with
///     every result, so a client cites pages and does not invent without first reading the schema page.
/// </summary>
internal static class KnowledgeConventions
{
    /// <summary>Slug of the schema page that holds the conventions.</summary>
    public const string SchemaSlug = "knowledge-schema";

    /// <summary>Heading of the section that holds the answering conventions.</summary>
    public const string SectionHeading = "Answering questions";

    /// <summary>Maximum length of the conventions text in a response.</summary>
    public const int MaxLength = 2000;

    /// <summary>Source of the built-in default.</summary>
    public const string DefaultSource = "default";

    /// <summary>Appended to a truncated text; counted in <see cref="MaxLength" />.</summary>
    public const string TruncationMarker = " … (truncated; read the page knowledge-schema with knowledge_get for the full text)";

    /// <summary>The conventions for tenants without a schema page or without the section.</summary>
    public const string DefaultText =
        "Answer only from this tenant's knowledge pages, not from general knowledge. Search with the words of the " +
        "question, read every page you use with knowledge_get and cite it by its slug in square brackets right after the " +
        "statement it supports, e.g. [page-slug]. Never cite a page you did not read. If the pages answer only part of " +
        "the question, answer that part and say what is missing. If no page answers the question, write " +
        "`No knowledge page answers this question.` and do not answer from general knowledge. After a useful answer, " +
        "offer to file it back as a new page (knowledge_upsert_page, origin Query, linked to the cited pages) and write " +
        "it only after the user confirms.";

    /// <summary>The built-in default.</summary>
    public static KnowledgeAnsweringConventions Default { get; } = new(DefaultText, DefaultSource);

    /// <summary>
    ///     Reads the conventions with the caller's <paramref name="session" />. Never throws: a failed read returns the
    ///     default, so a search never fails because of its conventions.
    /// </summary>
    public static async Task<KnowledgeAnsweringConventions> ReadAsync(ITenantRepository repository,
        IOctoSession session, string tenantId, KnowledgeConventionsCache? cache)
    {
        try
        {
            var page = await KnowledgePages.FindBySlugAsync(repository, session, KnowledgePages.PageType, SchemaSlug);
            if (page == null)
            {
                return Default;
            }

            // Only a Verified text speaks for the tenant (review of PR #66): every MCP write sets a page to Draft, so an
            // AI client with write rights could otherwise put its own instructions into every later search response.
            // A person verifies outside MCP. AB#8780: while the current version is Draft (an MCP edit not yet verified),
            // the newest Verified version of the page applies instead of the default - the text a person last approved
            // (kb-test2 2026-11-13: a Draft edit dropped the conventions to the default, eval 1/12).
            var content = KnowledgePages.ReadContent(page);
            var version = KnowledgePages.CurrentVersion(page);
            if (content.Status == KnowledgePages.StatusDraft)
            {
                var fallback = await NewestVerifiedVersionAsync(repository, session);
                if (fallback == null)
                {
                    return Default;
                }

                (content, version) = fallback.Value;
            }
            else if (content.Status != KnowledgePages.StatusVerified)
            {
                return Default;
            }

            var pageRtId = page.RtId.ToString();
            var source = $"{SchemaSlug}@{version.ToString(CultureInfo.InvariantCulture)}";
            if (cache != null && cache.TryGet(tenantId, pageRtId, version, out var cached))
            {
                return cached.Length == 0 ? Default : new KnowledgeAnsweringConventions(cached, source);
            }

            var section = content.Sections
                .FirstOrDefault(s => IsAnsweringHeading(s.Heading) && !string.IsNullOrWhiteSpace(s.SectionContent));
            var text = section == null ? string.Empty : Bound(section.SectionContent!);
            cache?.Set(tenantId, pageRtId, version, text);
            return text.Length == 0 ? Default : new KnowledgeAnsweringConventions(text, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Default;
        }
    }

    /// <summary>
    ///     The content and number of the newest Verified version of the schema page, read with the caller's session;
    ///     <c>null</c> when there is none, or when a newer version is Rejected (a person turned the text down later).
    /// </summary>
    public static async Task<(KnowledgeContent Content, int Version)?> NewestVerifiedVersionAsync(
        ITenantRepository repository, IOctoSession session)
    {
        var versions = await KnowledgePlatformUpdates.ListVersionsAsync(repository, session, SchemaSlug);
        foreach (var version in versions.OrderByDescending(v => v.Number))
        {
            var content = KnowledgePages.ReadContent(version.Entity);
            if (content.Status == KnowledgePages.StatusRejected)
            {
                return null;
            }

            if (content.Status == KnowledgePages.StatusVerified)
            {
                return (content, version.Number);
            }
        }

        return null;
    }

    /// <summary>
    ///     Bounds <paramref name="text" /> to <see cref="MaxLength" /> characters. A longer text is cut at the last
    ///     whitespace before the limit (or at the limit when there is none) and ends with <see cref="TruncationMarker" />;
    ///     the same input always gives the same output.
    /// </summary>
    public static string Bound(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= MaxLength)
        {
            return trimmed;
        }

        var budget = MaxLength - TruncationMarker.Length;
        var cut = trimmed.LastIndexOfAny([' ', '\t', '\r', '\n'], budget);
        var end = cut > budget / 2 ? cut : budget;
        // Never split a surrogate pair (an emoji or another non-BMP character at the hard cut).
        if (char.IsHighSurrogate(trimmed[end - 1]))
        {
            end--;
        }

        var head = trimmed[..end].TrimEnd();
        return head + TruncationMarker;
    }

    private static bool IsAnsweringHeading(string? heading) =>
        heading != null && heading.Trim().TrimStart('#').Trim()
            .Equals(SectionHeading, StringComparison.OrdinalIgnoreCase);
}
