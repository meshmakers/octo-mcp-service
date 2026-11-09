using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     The tenant knowledge base over MCP (AB#8484, Feature AB#8450): search, read, write, link, restore and review
///     knowledge pages (System.Ai 4.7.0 <c>KnowledgePage</c>). Family 2: the engine directly, with the caller's own
///     session, so the caller's CK roles and data permissions decide what may be read and written. Every write runs the
///     content guard (AB#8475) and appends a <c>KnowledgePageVersion</c>; pages are written only through these tools.
/// </summary>
[McpServerToolType]
public static class KnowledgeTools
{
    private const int MaxSearchCandidates = 200;

    /// <summary>Searches the knowledge pages of the tenant, best match first.</summary>
    [McpServerTool(Name = "knowledge_search")]
    [Description(
        "Search the tenant's knowledge base (knowledge pages). Returns pages best first with slug, title, kind, " +
        "verification status, summary, current version and the resource URI. With a query only pages that contain at least " +
        "one of its words or an inflected form of it (plural, -ing, -ed) are returned; no match is an empty result with " +
        "the message to say so. Words shorter than 3 " +
        "characters are ignored; a query without a longer word returns the index of all pages, like no query (sorted by " +
        "title). Read the conventions page 'knowledge-schema' first; answer questions from pages and cite them by slug.")]
    public static async Task<KnowledgeSearchResponse> KnowledgeSearch(
        McpServer server,
        [Description("Words to search for (title, summary and section content; words of 3 or more characters). Empty: " +
                     "list all pages.")]
        string? query = null,
        [Description("Only pages of this kind: Concept, Entity, HowTo, Summary, Skill, Schema.")]
        string? kind = null,
        [Description("Only pages with this verification status: Draft, InReview, Verified, Rejected.")]
        string? status = null,
        [Description("Maximum number of pages (default 10, max 100).")]
        int limit = 10,
        [Description("Tenant to search. Falls back to URL route.")]
        string? tenantId = null)
    {
        if (limit is < 1 or > 100)
        {
            return new KnowledgeSearchResponse { ErrorMessage = "limit must be between 1 and 100." };
        }

        int? kindFilter = null, statusFilter = null;
        if (kind != null && (kindFilter = KnowledgePages.ParseEnum(kind, KnowledgePages.KindNames)) == null)
        {
            return new KnowledgeSearchResponse { ErrorMessage = $"Unknown kind '{kind}'." };
        }

        if (status != null && (statusFilter = KnowledgePages.ParseEnum(status, KnowledgePages.StatusNames)) == null)
        {
            return new KnowledgeSearchResponse { ErrorMessage = $"Unknown status '{status}'." };
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeSearchResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            // AB#8566: a query searches only when it has a word of 3+ characters; otherwise it is the index (no query).
            var terms = Terms(query);
            var isSearch = terms.Length > 0;
            var candidates = await KnowledgePages.ListPagesAsync(ctx.Repo, session, isSearch ? query : null,
                MaxSearchCandidates);
            var pages = candidates
                .Select(p => (Page: p, Content: KnowledgePages.ReadContent(p)))
                .Where(x => kindFilter == null || x.Content.Kind == kindFilter)
                .Where(x => statusFilter == null || x.Content.Status == statusFilter)
                .Select(x => KnowledgePages.Summarize(ctx.TenantId!, x.Page, Score(x.Content, terms)))
                // A page without any query word or its English stem (score 0) does not match, whatever the text index
                // preselected.
                .Where(p => !isSearch || p.Score > 0)
                .OrderByDescending(p => p.Score).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            var ignoredQuery = !isSearch && !string.IsNullOrWhiteSpace(query);
            return new KnowledgeSearchResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Pages = pages,
                Message = pages.Count == 0
                    ? "No knowledge page matches. Say so instead of answering from general knowledge."
                    : ignoredQuery
                        ? $"The query has no word of 3 or more characters, so this is the index of all pages, not a match: {pages.Count} page(s)."
                        : $"{pages.Count} page(s)."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeSearchResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Reads a knowledge page (or one of its versions) with its markdown, links and sources.</summary>
    [McpServerTool(Name = "knowledge_get")]
    [Description(
        "Read a knowledge page by slug: the page rendered as markdown plus its sections, citations, flagged claims, the " +
        "slugs it links to and the source artifacts it was generated from. Pass version to read an older version.")]
    public static async Task<KnowledgePageResponse> KnowledgeGet(
        McpServer server,
        [Description("Slug of the page, e.g. 'knowledge-schema'.")] string slug,
        [Description("Version to read; omit for the current version.")] int? version = null,
        [Description("Tenant to read from. Falls back to URL route.")] string? tenantId = null)
    {
        if (!KnowledgePages.IsValidSlug(slug))
        {
            return new KnowledgePageResponse { ErrorMessage = $"Invalid slug '{slug}'.", ErrorCode = "InvalidArgument" };
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgePageResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var page = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, slug);
            if (page == null)
            {
                return NotFound<KnowledgePageResponse>(ctx.TenantId!, $"Knowledge page '{slug}' not found.");
            }

            var shownVersion = version ?? KnowledgePages.CurrentVersion(page);
            var source = page;
            if (version != null && version != KnowledgePages.CurrentVersion(page))
            {
                source = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.VersionType,
                    KnowledgePages.VersionName(slug, version.Value));
                if (source == null)
                {
                    return NotFound<KnowledgePageResponse>(ctx.TenantId!, $"Version {version} of '{slug}' not found.");
                }
            }

            var content = KnowledgePages.ReadContent(source);
            var linkTargets = await KnowledgePages.OutboundTargetsAsync(ctx.Repo, session, page.RtId,
                KnowledgePages.LinksToRole);
            var linkedSlugs = new List<string>();
            foreach (var target in linkTargets)
            {
                var linked = await ctx.Repo.GetRtEntityByRtIdAsync(session,
                    new RtEntityId(KnowledgePages.PageTypeId, target));
                if (linked?.RtWellKnownName != null)
                {
                    linkedSlugs.Add(linked.RtWellKnownName);
                }
            }

            var sources = await KnowledgePages.OutboundTargetsAsync(ctx.Repo, session, page.RtId,
                KnowledgePages.GeneratedFromRole);

            return new KnowledgePageResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Page = KnowledgePages.Summarize(ctx.TenantId!, page),
                Version = shownVersion,
                Markdown = KnowledgePages.Render(content, slug, shownVersion),
                Sections = content.Sections.ToList(),
                Citations = content.Citations.ToList(),
                HallucinationFlags = content.HallucinationFlags.ToList(),
                LinksTo = linkedSlugs.Order().ToList(),
                GeneratedFrom = sources.Select(s => s.ToString()).ToList()
            };
        }
        catch (Exception ex)
        {
            return new KnowledgePageResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Creates or updates a knowledge page; every write is a new version.</summary>
    [McpServerTool(Name = "knowledge_upsert_page")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create or update a knowledge page. Takes the generation JSON of the knowledge prompts unchanged: title, sections " +
        "[{heading, sectionContent, sortOrder}], citations [{sectionRef, sourceQuote, sourceAnchor, sourceRtId}] and " +
        "optional hallucinationFlags [{claim, reason}]. Every write is a new version (the page keeps its history); the page " +
        "becomes Draft again until a person verifies it. Cited SourceArtifacts are linked with GeneratedFrom. Content that " +
        "looks like a credential or a secret value of the tenant is refused with ContentRejected. Pass expectedVersion " +
        "(the version you read) to refuse the write when someone changed the page in between.")]
    public static async Task<KnowledgeWriteResponse> KnowledgeUpsertPage(
        McpServer server,
        [Description("Slug of the page: lower-case words joined by hyphens, e.g. 'rollout-seed-change'.")] string slug,
        [Description("Title of the page.")] string title,
        [Description("Sections in order: heading, sectionContent (markdown), sortOrder.")]
        List<KnowledgeSectionDto> sections,
        [Description("Quote-level citations: sectionRef, sourceQuote, sourceAnchor, sourceRtId.")]
        List<KnowledgeCitationDto>? citations = null,
        [Description("Claims the verification pass could not ground: claim, reason.")]
        List<KnowledgeHallucinationFlagDto>? hallucinationFlags = null,
        [Description("One or two sentences that say what the page is about (shown in search and the index).")]
        string? summary = null,
        [Description("Kind: Concept (default), Entity, HowTo, Summary, Skill, Schema. Unchanged on update when omitted.")]
        string? kind = null,
        [Description("Origin of a new page: Manual (default), Query (an answer filed back on the user's confirmation), Ingest.")]
        string? origin = null,
        [Description("Further SourceArtifact rtIds to link with GeneratedFrom (cited sourceRtIds are linked anyway).")]
        List<string>? sourceRtIds = null,
        [Description("Slugs of pages this page links to (LinksTo), e.g. the pages an answer cites.")]
        List<string>? linksTo = null,
        [Description("Why this version is written, e.g. 'filed back from a question'.")] string? changeNote = null,
        [Description("The current version you read; the write is refused when the page has moved on.")]
        int? expectedVersion = null,
        [Description("Name of the prompt template that generated the content (run metadata).")] string? promptName = null,
        [Description("Version of that prompt template.")] int? promptVersion = null,
        [Description("Model id that generated the content.")] string? modelId = null,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        if (!KnowledgePages.IsValidSlug(slug))
        {
            return Invalid($"Invalid slug '{slug}': lower-case letters, digits, '-', '.', '_' (2-120 characters).");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Invalid("title is required.");
        }

        if (sections == null || sections.Count == 0 || sections.Any(s => string.IsNullOrWhiteSpace(s.Heading)))
        {
            return Invalid("sections must hold at least one section, each with a heading.");
        }

        int? kindValue = null;
        if (kind != null && (kindValue = KnowledgePages.ParseEnum(kind, KnowledgePages.KindNames)) == null)
        {
            return Invalid($"Unknown kind '{kind}'.");
        }

        var originValue = origin == null ? 2 : KnowledgePages.ParseEnum(origin, KnowledgePages.OriginNames);
        if (originValue == null || originValue == 3)
        {
            return Invalid($"Origin '{origin}' is not allowed: use Manual, Query or Ingest (Blueprint pages come from blueprints).");
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeWriteResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var existing = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, slug);
            var conflict = CheckVersion(existing, expectedVersion, ctx.TenantId!);
            if (conflict != null)
            {
                return conflict;
            }

            var content = new KnowledgeContent(title.Trim(),
                kindValue ?? (existing == null ? 0 : KnowledgePages.ReadContent(existing).Kind),
                summary, sections.OrderBy(s => s.SortOrder).ToList(), citations ?? [], hallucinationFlags ?? [],
                KnowledgePages.StatusDraft);

            var refusal = await GuardAsync(server, ctx, session, content, slug, "knowledge_upsert_page", cancellationToken,
                new ContentGuardField("changeNote", changeNote), new ContentGuardField("promptName", promptName),
                new ContentGuardField("modelId", modelId));
            if (refusal != null)
            {
                return refusal;
            }

            var write = KnowledgePages.BuildWrite(existing, slug, content, originValue.Value, ctx.Author!,
                changeNote ?? (existing == null ? "created" : "updated"), null,
                new KnowledgeRunMetadata(promptName, promptVersion, modelId), DateTime.UtcNow);

            var associations = new List<AssociationUpdateInfo> { KnowledgePages.VersionOf(write.Version, write.Page) };
            var response = new KnowledgeWriteResponse();
            await AddSourceLinksAsync(ctx.Repo, session, write.Page, existing,
                content.Citations.Select(c => c.SourceRtId).Concat(sourceRtIds ?? []), associations, response);
            var missingLinks = await AddPageLinksAsync(ctx.Repo, session, write.Page, existing, linksTo ?? [],
                associations);
            if (missingLinks.Count > 0)
            {
                return Invalid($"Linked page(s) not found: {string.Join(", ", missingLinks)}.");
            }

            var failure = await ApplyAsync(ctx.Repo, session, ctx.TenantId!, write.Updates, associations, slug, existing);
            if (failure != null)
            {
                return failure;
            }

            response.IsSuccess = true;
            response.TenantId = ctx.TenantId;
            response.Created = existing == null;
            response.Page = KnowledgePages.Summarize(ctx.TenantId!, write.Page);
            response.Message = $"{(existing == null ? "Created" : "Updated")} '{slug}' as version {write.VersionNumber} (Draft).";
            return response;
        }
        catch (Exception ex)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Links (or unlinks) two knowledge pages.</summary>
    [McpServerTool(Name = "knowledge_link")]
    [Description(
        "Link a knowledge page to another one (LinksTo), e.g. a page to the pages it mentions. remove=true removes the " +
        "link. Links are not page content and create no version.")]
    public static async Task<KnowledgeWriteResponse> KnowledgeLink(
        McpServer server,
        [Description("Slug of the page the link starts at.")] string fromSlug,
        [Description("Slug of the linked page.")] string toSlug,
        [Description("true removes the link.")] bool remove = false,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null)
    {
        if (!KnowledgePages.IsValidSlug(fromSlug) || !KnowledgePages.IsValidSlug(toSlug) || fromSlug == toSlug)
        {
            return Invalid("fromSlug and toSlug must be two different valid slugs.");
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeWriteResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var from = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, fromSlug);
            var to = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, toSlug);
            if (from == null || to == null)
            {
                return NotFound<KnowledgeWriteResponse>(ctx.TenantId!,
                    $"Knowledge page '{(from == null ? fromSlug : toSlug)}' not found.");
            }

            var existing = await KnowledgePages.OutboundTargetsAsync(ctx.Repo, session, from.RtId,
                KnowledgePages.LinksToRole);
            var linked = existing.Contains(to.RtId);
            if (linked != remove)
            {
                return new KnowledgeWriteResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Page = KnowledgePages.Summarize(ctx.TenantId!, from),
                    Message = remove ? $"'{fromSlug}' does not link to '{toSlug}'." : $"'{fromSlug}' already links to '{toSlug}'."
                };
            }

            var origin = new RtEntityId(KnowledgePages.PageTypeId, from.RtId);
            var target = new RtEntityId(KnowledgePages.PageTypeId, to.RtId);
            var role = new RtCkId<CkAssociationRoleId>(KnowledgePages.LinksToRole);
            var failure = await ApplyAsync(ctx.Repo, session, ctx.TenantId!, [],
                [remove ? AssociationUpdateInfo.CreateDelete(origin, target, role) : AssociationUpdateInfo.CreateInsert(origin, target, role)]);
            return failure ?? new KnowledgeWriteResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Page = KnowledgePages.Summarize(ctx.TenantId!, from),
                Message = remove ? $"Removed link '{fromSlug}' -> '{toSlug}'." : $"Linked '{fromSlug}' -> '{toSlug}'."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Makes an older version current again by writing it as a new version.</summary>
    [McpServerTool(Name = "knowledge_restore_version")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Restore an older version of a knowledge page: its content becomes current as a new version (nothing is deleted, " +
        "the history stays complete). The restored page is Draft until a person verifies it.")]
    public static async Task<KnowledgeWriteResponse> KnowledgeRestoreVersion(
        McpServer server,
        [Description("Slug of the page.")] string slug,
        [Description("The version to restore.")] int version,
        [Description("The current version you read; the restore is refused when the page has moved on.")]
        int? expectedVersion = null,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        if (!KnowledgePages.IsValidSlug(slug) || version < 1)
        {
            return Invalid("A valid slug and a version of at least 1 are required.");
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeWriteResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var page = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, slug);
            if (page == null)
            {
                return NotFound<KnowledgeWriteResponse>(ctx.TenantId!, $"Knowledge page '{slug}' not found.");
            }

            var conflict = CheckVersion(page, expectedVersion, ctx.TenantId!);
            if (conflict != null)
            {
                return conflict;
            }

            var old = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.VersionType,
                KnowledgePages.VersionName(slug, version));
            if (old == null)
            {
                return NotFound<KnowledgeWriteResponse>(ctx.TenantId!, $"Version {version} of '{slug}' not found.");
            }

            var content = KnowledgePages.ReadContent(old) with { Status = KnowledgePages.StatusDraft };
            var refusal = await GuardAsync(server, ctx, session, content, slug, "knowledge_restore_version",
                cancellationToken);
            if (refusal != null)
            {
                return refusal;
            }

            var write = KnowledgePages.BuildWrite(page, slug, content, 0, ctx.Author!, $"restore of version {version}",
                version, null, DateTime.UtcNow);
            var failure = await ApplyAsync(ctx.Repo, session, ctx.TenantId!, write.Updates,
                [KnowledgePages.VersionOf(write.Version, write.Page)], slug, page);
            return failure ?? new KnowledgeWriteResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Page = KnowledgePages.Summarize(ctx.TenantId!, write.Page),
                Message = $"Restored version {version} of '{slug}' as version {write.VersionNumber} (Draft)."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>The octo-cli command a person uses to set a status outside MCP (named in descriptions and refusals).</summary>
    internal const string HumanStatusCommand = "octo-cli -c SetKnowledgePageStatus -s <slug> -st Verified";

    /// <summary>
    ///     Moves a page through the review states Draft, InReview and Rejected. Verified is never set over MCP (decision
    ///     G2a, AB#8565): MCP callers are always AI clients, also with a user's token; a person sets Verified outside MCP.
    /// </summary>
    [McpServerTool(Name = "knowledge_set_status")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Set the verification status of a knowledge page: Draft, InReview or Rejected. Verified is never set over MCP, " +
        "for any caller (error VerifiedNotAllowedOverMcp): a person verifies a page outside MCP with octo-cli " +
        "(SetKnowledgePageStatus -s <slug> -st Verified) or GraphQL. Each change is a new version with the same content. " +
        "Pages are not deleted over MCP: set an unwanted page to Rejected; a person deletes pages outside MCP (GraphQL or " +
        "octo-cli).")]
    public static async Task<KnowledgeWriteResponse> KnowledgeSetStatus(
        McpServer server,
        [Description("Slug of the page.")] string slug,
        [Description("Draft, InReview or Rejected. Verified is refused over MCP; a person sets it with octo-cli " +
                     "SetKnowledgePageStatus or GraphQL.")]
        string status,
        [Description("Why, e.g. 'checked against the runbook'.")] string? changeNote = null,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var statusValue = KnowledgePages.ParseEnum(status, KnowledgePages.StatusNames);
        if (!KnowledgePages.IsValidSlug(slug) || statusValue == null)
        {
            return Invalid("A valid slug and a status (Draft, InReview, Rejected) are required.");
        }

        // G2a: refused for every caller (user token, client token, no principal), before the tenant is opened.
        if (statusValue == KnowledgePages.StatusVerified)
        {
            return new KnowledgeWriteResponse
            {
                ErrorCode = "VerifiedNotAllowedOverMcp",
                ErrorMessage = "Verified is never set over MCP: MCP callers are AI clients, also in a user's session. " +
                               "Leave the page in Draft or InReview; a person verifies it outside MCP with " +
                               $"'{HumanStatusCommand}' or a GraphQL client."
            };
        }

        var ctx = await OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgeWriteResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var page = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, slug);
            if (page == null)
            {
                return NotFound<KnowledgeWriteResponse>(ctx.TenantId!, $"Knowledge page '{slug}' not found.");
            }

            var content = KnowledgePages.ReadContent(page) with { Status = statusValue.Value };
            var refusal = await GuardAsync(server, ctx, session, content, slug, "knowledge_set_status", cancellationToken,
                new ContentGuardField("changeNote", changeNote));
            if (refusal != null)
            {
                return refusal;
            }

            var write = KnowledgePages.BuildWrite(page, slug, content, 0, ctx.Author!,
                changeNote ?? $"status {KnowledgePages.Name(KnowledgePages.StatusNames, statusValue.Value)}", null, null,
                DateTime.UtcNow);
            var failure = await ApplyAsync(ctx.Repo, session, ctx.TenantId!, write.Updates,
                [KnowledgePages.VersionOf(write.Version, write.Page)], slug, page);
            return failure ?? new KnowledgeWriteResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Page = KnowledgePages.Summarize(ctx.TenantId!, write.Page),
                Message = $"'{slug}' is {KnowledgePages.Name(KnowledgePages.StatusNames, statusValue.Value)} (version {write.VersionNumber})."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    internal sealed record KnowledgeContext(
        ITenantRepository? Repository,
        RtSecurityContext? Security,
        string? TenantId,
        string? Author,
        string? Error)
    {
        /// <summary>The repository of a context without error.</summary>
        public ITenantRepository Repo => Repository ?? throw new InvalidOperationException("No repository.");
    }

    internal static async Task<KnowledgeContext> OpenAsync(McpServer server, string? tenantId)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new KnowledgeContext(null, null, security.ResolvedTenantId, null, security.Error);
        }

        var repository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(ckCache);
        if (!ckCache.TryGetRtCkType(repository.TenantId, KnowledgePages.PageTypeId, out _))
        {
            return new KnowledgeContext(null, null, repository.TenantId, null,
                $"Tenant '{repository.TenantId}' has no knowledge base: it needs System.Ai 4.7.0 or later (blueprint " +
                "System.Ai.Default 2.1.0, applied on EnableAi).");
        }

        var principal = McpCallerPrincipal.FromServer(server);
        var author = principal == null
            ? "unknown"
            : principal.IsUserPrincipal ? $"user:{principal.UserSubjectId}" : $"client:{principal.ClientId}";
        return new KnowledgeContext(repository, security.SecurityContext, repository.TenantId, author, null);
    }

    private static async Task<KnowledgeWriteResponse?> GuardAsync(McpServer server, KnowledgeContext ctx,
        IOctoSession session, KnowledgeContent content, string slug, string operation, CancellationToken ct,
        params ContentGuardField[] extraFields)
    {
        var guard = server.Services!.GetService<IContentGuard>();
        if (guard == null)
        {
            // Fail closed: the knowledge tools never write without the guard.
            return new KnowledgeWriteResponse
            {
                TenantId = ctx.TenantId,
                ErrorCode = ContentGuardResult.ErrorCode,
                ErrorMessage = "The content guard is not configured; knowledge writes are refused."
            };
        }

        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        var protector = server.Services!.GetService<ISecretAttributeProtector>();
        var context = new ContentGuardContext(ctx.TenantId!, ctx.Author!, operation,
            token => TenantSecretFingerprints.ReadAsync(ctx.Repo, session, ckCache, protector, token));
        var fields = KnowledgePages.GuardFields(content, slug).Concat(extraFields.Where(f => f.Text != null)).ToList();
        var result = await guard.CheckAsync(context, fields, ct);
        return result.IsRejected
            ? new KnowledgeWriteResponse
            {
                TenantId = ctx.TenantId,
                ErrorCode = ContentGuardResult.ErrorCode,
                ErrorMessage = result.Message
            }
            : null;
    }

    private static KnowledgeWriteResponse? CheckVersion(RtEntity? existing, int? expectedVersion, string tenantId)
    {
        if (expectedVersion == null)
        {
            return null;
        }

        var current = existing == null ? 0 : KnowledgePages.CurrentVersion(existing);
        return current == expectedVersion
            ? null
            : new KnowledgeWriteResponse
            {
                TenantId = tenantId,
                IsConflict = true,
                ErrorCode = "VersionConflict",
                Page = existing == null ? null : KnowledgePages.Summarize(tenantId, existing),
                ErrorMessage = $"The page is at version {current}, not {expectedVersion}; read it again and rebase the change."
            };
    }

    private static async Task AddSourceLinksAsync(ITenantRepository repository, IOctoSession session, RtEntity page,
        RtEntity? existing, IEnumerable<string?> rtIds, List<AssociationUpdateInfo> associations,
        KnowledgeWriteResponse response)
    {
        var linked = existing == null
            ? []
            : await KnowledgePages.OutboundTargetsAsync(repository, session, page.RtId, KnowledgePages.GeneratedFromRole);
        foreach (var rtId in rtIds.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()).Distinct())
        {
            var sourceTypeId = new RtCkId<CkTypeId>(KnowledgePages.SourceArtifactType);
            var source = OctoObjectId.TryParse(rtId, out var id)
                ? await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(sourceTypeId, id))
                : null;
            if (source == null)
            {
                response.UnresolvedSources.Add(rtId);
                continue;
            }

            response.LinkedSources.Add(rtId);
            if (!linked.Contains(id))
            {
                associations.Add(AssociationUpdateInfo.CreateInsert(new RtEntityId(KnowledgePages.PageTypeId, page.RtId),
                    new RtEntityId(sourceTypeId, id), new RtCkId<CkAssociationRoleId>(KnowledgePages.GeneratedFromRole)));
            }
        }
    }

    private static async Task<List<string>> AddPageLinksAsync(ITenantRepository repository, IOctoSession session,
        RtEntity page, RtEntity? existing, IEnumerable<string> slugs, List<AssociationUpdateInfo> associations)
    {
        var missing = new List<string>();
        var linked = existing == null
            ? []
            : await KnowledgePages.OutboundTargetsAsync(repository, session, page.RtId, KnowledgePages.LinksToRole);
        foreach (var slug in slugs.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
        {
            var target = await KnowledgePages.FindBySlugAsync(repository, session, KnowledgePages.PageType, slug);
            if (target == null)
            {
                missing.Add(slug);
                continue;
            }

            if (!linked.Contains(target.RtId) && target.RtId != page.RtId)
            {
                associations.Add(AssociationUpdateInfo.CreateInsert(new RtEntityId(KnowledgePages.PageTypeId, page.RtId),
                    new RtEntityId(KnowledgePages.PageTypeId, target.RtId),
                    new RtCkId<CkAssociationRoleId>(KnowledgePages.LinksToRole)));
            }
        }

        return missing;
    }

    /// <summary>
    ///     Applies one write in a transaction. For a page write (<paramref name="slug" /> set) the page is read again
    ///     inside the transaction first: when it moved on since <paramref name="readPage" /> was read (another writer
    ///     committed in between) nothing is written and the result is <c>VersionConflict</c>. A writer that commits
    ///     after that re-read makes this transaction's page update a MongoDB write conflict, which maps to
    ///     <c>VersionConflict</c> as well, so two writers can never both write version <c>n + 1</c> of an existing page.
    /// </summary>
    /// <returns>The failure response, or <c>null</c> when the write was committed.</returns>
    private static async Task<KnowledgeWriteResponse?> ApplyAsync(ITenantRepository repository, IOctoSession session,
        string tenantId, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
        IReadOnlyList<AssociationUpdateInfo> associations, string? slug = null, RtEntity? readPage = null)
    {
        var readVersion = readPage == null ? 0 : KnowledgePages.CurrentVersion(readPage);
        session.StartTransaction();
        var result = new OperationResult();
        try
        {
            if (slug != null)
            {
                var current = await KnowledgePages.FindBySlugAsync(repository, session, KnowledgePages.PageType, slug);
                var currentVersion = current == null ? 0 : KnowledgePages.CurrentVersion(current);
                if ((current == null) != (readPage == null) || currentVersion != readVersion)
                {
                    await session.AbortTransactionAsync();
                    return Conflict(tenantId, current, readVersion);
                }
            }

            await repository.ApplyChangesAsync(session, updates, associations, result);
            if (result.HasErrors || result.HasFatalErrors)
            {
                await session.AbortTransactionAsync();
                var messages = result.GetMessages();
                return IsWriteConflict(messages)
                    ? Conflict(tenantId, null, readVersion)
                    : new KnowledgeWriteResponse { TenantId = tenantId, ErrorMessage = $"The write failed: {messages}" };
            }

            await session.CommitTransactionAsync();
            return null;
        }
        catch (Exception ex) when (IsWriteConflict(ex))
        {
            await TryAbortAsync(session);
            return Conflict(tenantId, null, readVersion);
        }
        catch
        {
            await TryAbortAsync(session);
            throw;
        }
    }

    private static KnowledgeWriteResponse Conflict(string tenantId, RtEntity? current, int readVersion) => new()
    {
        TenantId = tenantId,
        IsConflict = true,
        ErrorCode = "VersionConflict",
        Page = current == null ? null : KnowledgePages.Summarize(tenantId, current),
        ErrorMessage = $"The page changed while this write ran (it was at version {readVersion}); nothing was written. " +
                       "Read it again and rebase the change."
    };

    /// <summary>
    ///     A MongoDB write conflict (server error 112, label <c>TransientTransactionError</c>). The engine passes the
    ///     driver exception through (or wraps it), and the driver message names the error.
    /// </summary>
    internal static bool IsWriteConflict(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (IsWriteConflict(e.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWriteConflict(string? message) =>
        message != null && (message.Contains("WriteConflict", StringComparison.OrdinalIgnoreCase) ||
                            message.Contains("TransientTransactionError", StringComparison.Ordinal));

    private static async Task TryAbortAsync(IOctoSession session)
    {
        try
        {
            await session.AbortTransactionAsync();
        }
        catch (Exception)
        {
            // Already committed or aborted (e.g. a commit that failed with a write conflict); the caller reports the
            // original failure, an abort error must not replace it.
        }
    }

    private static string[] Terms(string? query) => string.IsNullOrWhiteSpace(query)
        ? []
        : query.Split([' ', ',', ';', '?', '!', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2).Select(t => t.ToLowerInvariant()).Distinct().ToArray();

    /// <summary>
    ///     Title matches weigh 3, summary 2, section text 1 (same weights as the text index). A term counts its literal
    ///     occurrences; where it has none, it counts the words with the same English stem (AB#8566: the text index stems,
    ///     so "pipelines" matches "pipeline" there and must not score 0 here).
    /// </summary>
    internal static double Score(KnowledgeContent content, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0)
        {
            return 0;
        }

        var title = Field(content.Title);
        var summary = Field(content.Summary ?? string.Empty);
        var body = Field(string.Join("\n", content.Sections.Select(s => s.Heading + " " + s.SectionContent)));
        return terms.Sum(t =>
        {
            var stem = KnowledgeStemmer.Stem(t);
            return 3 * Hits(title, t, stem) + 2 * Hits(summary, t, stem) + Math.Min(Hits(body, t, stem), 5);
        });

        static (string Text, string[] Stems) Field(string text) =>
            (text.ToLowerInvariant(), KnowledgeStemmer.Words(text).Select(KnowledgeStemmer.Stem).ToArray());

        static int Hits((string Text, string[] Stems) field, string term, string stem)
        {
            var literal = Count(field.Text, term);
            return literal > 0 ? literal : field.Stems.Count(s => s == stem);
        }

        static int Count(string text, string term)
        {
            var n = 0;
            for (var i = text.IndexOf(term, StringComparison.Ordinal); i >= 0; i = text.IndexOf(term, i + term.Length, StringComparison.Ordinal))
            {
                n++;
            }

            return n;
        }
    }

    private static KnowledgeWriteResponse Invalid(string message) =>
        new() { ErrorCode = "InvalidArgument", ErrorMessage = message };

    private static T NotFound<T>(string tenantId, string message) where T : KnowledgeResponse, new() =>
        new() { TenantId = tenantId, ErrorCode = "NotFound", ErrorMessage = message };
}
