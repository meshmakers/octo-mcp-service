using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AB#8780: platform updates of Blueprint knowledge pages. A blueprint update (System.Ai.Builder, System.Ai.Default)
///     brings new <c>KnowledgePageVersion</c> entities but never refreshes an existing page (AB#8778). The knowledge
///     maintenance completes the rule with these tools:
///     <list type="bullet">
///         <item><c>knowledge_list_platform_updates</c> (read) — what the lint reports as <c>PlatformUpdateAvailable</c>;</item>
///         <item>
///             <c>knowledge_adopt_platform_updates</c> (write) — switches every unedited page to its newest Blueprint
///             version, without a new version, keeping the status that version brings;
///         </item>
///         <item><c>knowledge_dismiss_platform_update</c> (write) — a person keeps the edited page as it is.</item>
///     </list>
///     Adopting an update for an edited page is the existing restore (<c>knowledge_restore_version</c>, octo-cli). All
///     writes carry the write channel <see cref="KnowledgePages.WriteChannel" /> (AB#8575). No tool here sets Verified on
///     anything the tenant wrote (AB#8565): the adopt copies the status of a seeded Blueprint version onto a page whose
///     current version is itself a seeded Blueprint version, and refuses every other page.
/// </summary>
[McpServerToolType]
public static class KnowledgePlatformUpdateTools
{
    /// <summary>Lists the pages with a newer Blueprint version.</summary>
    [McpServerTool(Name = "knowledge_list_platform_updates")]
    [Description(
        "List knowledge pages for which a blueprint update brought a newer Blueprint version (System.Ai.Builder, " +
        "System.Ai.Default). state Adoptable: the page was never edited in the tenant (its current version is a Blueprint " +
        "version); knowledge_adopt_platform_updates switches it. state Available: the page was edited in the tenant and " +
        "keeps its content (the knowledge lint reports PlatformUpdateAvailable); a person adopts the newer version with " +
        "knowledge_restore_version or dismisses it with knowledge_dismiss_platform_update. Deleted pages are not listed. " +
        "Changes nothing.")]
    public static async Task<KnowledgePlatformUpdatesResponse> KnowledgeListPlatformUpdates(
        McpServer server,
        [Description("Tenant to read from. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await KnowledgeTools.OpenAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new KnowledgePlatformUpdatesResponse { ErrorMessage = ctx.Error };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var updates = await KnowledgePlatformUpdates.FindAsync(ctx.Repo, session);
            var response = new KnowledgePlatformUpdatesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Updates = updates.Select(Dto).ToList()
            };
            response.Message = $"{response.Updates.Count(u => u.State == nameof(PlatformUpdateState.Adoptable))} " +
                               "unedited page(s) to adopt, " +
                               $"{response.Updates.Count(u => u.State == nameof(PlatformUpdateState.Available))} edited " +
                               "page(s) with a newer Blueprint version.";
            return response;
        }
        catch (Exception ex)
        {
            return new KnowledgePlatformUpdatesResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Switches every unedited Blueprint page to its newest Blueprint version.</summary>
    [McpServerTool(Name = "knowledge_adopt_platform_updates")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Adopt platform updates of knowledge pages: every page whose current version is a Blueprint version (never " +
        "edited in the tenant) and that has a newer Blueprint version is switched to the newest one. The page takes " +
        "title, kind, summary, sections, citations and status from that version, CurrentVersion points to it (no new " +
        "version is written), LinksTo follows the links the version declares, and AdoptedByRun records runRef. Edited " +
        "pages are never changed (see knowledge_list_platform_updates). The content guard checks every page before it " +
        "is written. A second call without a new blueprint version writes nothing. Needs System.Ai 4.10.0.")]
    public static async Task<KnowledgeAdoptPlatformUpdatesResponse> KnowledgeAdoptPlatformUpdates(
        McpServer server,
        [Description("Reference of the run that adopts (the maintenance pipeline run, up to 200 characters); stored on " +
                     "every switched page as AdoptedByRun.")]
        string runRef,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var runError = KnowledgeTools.ValidateRunData(null, null, null, null, runRef);
        if (runRef == null || runError != null)
        {
            return new KnowledgeAdoptPlatformUpdatesResponse
            {
                ErrorCode = "InvalidArgument",
                ErrorMessage = runError ?? "runRef is required: the reference of the run that adopts."
            };
        }

        var ctx = await KnowledgeTools.OpenAsync(server, tenantId, write: true);
        if (ctx.Error != null)
        {
            return new KnowledgeAdoptPlatformUpdatesResponse { ErrorMessage = ctx.Error };
        }

        var modelError = RequireModel(server, ctx.TenantId!);
        if (modelError != null)
        {
            return new KnowledgeAdoptPlatformUpdatesResponse { TenantId = ctx.TenantId, ErrorMessage = modelError };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var response = new KnowledgeAdoptPlatformUpdatesResponse { TenantId = ctx.TenantId, RunRef = runRef };
            foreach (var update in await KnowledgePlatformUpdates.FindAsync(ctx.Repo, session))
            {
                if (update.State == PlatformUpdateState.Available)
                {
                    response.Available.Add(Dto(update));
                    continue;
                }

                var outcome = await AdoptAsync(server, ctx, session, update, runRef, cancellationToken);
                if (outcome.IsSuccess)
                {
                    response.Adopted.Add(outcome);
                }
                else
                {
                    response.Skipped.Add(outcome);
                }
            }

            response.IsSuccess = true;
            response.Message = $"Adopted {response.Adopted.Count} page(s), skipped {response.Skipped.Count}; " +
                               $"{response.Available.Count} edited page(s) keep their content (PlatformUpdateAvailable).";
            return response;
        }
        catch (Exception ex)
        {
            return new KnowledgeAdoptPlatformUpdatesResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Keeps an edited page and suppresses the finding until a newer Blueprint version arrives.</summary>
    [McpServerTool(Name = "knowledge_dismiss_platform_update")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Dismiss a platform update of an edited knowledge page: the page keeps its content and the knowledge lint stops " +
        "reporting PlatformUpdateAvailable until a Blueprint version with a higher number arrives. version is the Blueprint " +
        "version to dismiss (from knowledge_list_platform_updates or the lint finding). Changes no content, writes no " +
        "version and no status. To take the update instead, restore that version with knowledge_restore_version. Needs " +
        "System.Ai 4.10.0.")]
    public static async Task<KnowledgeWriteResponse> KnowledgeDismissPlatformUpdate(
        McpServer server,
        [Description("Slug of the page.")] string slug,
        [Description("The Blueprint version to dismiss.")] int version,
        [Description("Tenant to write to. Falls back to URL route.")] string? tenantId = null)
    {
        if (!KnowledgePages.IsValidSlug(slug) || version < 1)
        {
            return Invalid("A valid slug and a version of at least 1 are required.");
        }

        var ctx = await KnowledgeTools.OpenAsync(server, tenantId, write: true);
        if (ctx.Error != null)
        {
            return new KnowledgeWriteResponse { ErrorMessage = ctx.Error };
        }

        var modelError = RequireModel(server, ctx.TenantId!);
        if (modelError != null)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = modelError };
        }

        try
        {
            using var session = await ctx.Repo.GetSessionAsync(ctx.Security!);
            var page = await KnowledgePages.FindBySlugAsync(ctx.Repo, session, KnowledgePages.PageType, slug);
            if (page == null)
            {
                return new KnowledgeWriteResponse
                {
                    TenantId = ctx.TenantId, ErrorCode = "NotFound", ErrorMessage = $"Knowledge page '{slug}' not found."
                };
            }

            var versions = await KnowledgePlatformUpdates.ListVersionsAsync(ctx.Repo, session, slug);
            if (!versions.Any(v => v.IsBlueprint && v.Number == version))
            {
                return new KnowledgeWriteResponse
                {
                    TenantId = ctx.TenantId,
                    ErrorCode = "NotFound",
                    ErrorMessage = $"'{slug}' has no Blueprint version {version}; only a version a blueprint brought can be dismissed."
                };
            }

            var dismissed = KnowledgePlatformUpdates.Dismissed(page);
            if (dismissed >= version)
            {
                return new KnowledgeWriteResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Page = KnowledgePages.Summarize(ctx.TenantId!, page),
                    Message = $"Blueprint version {dismissed} of '{slug}' is already dismissed."
                };
            }

            var update = new RtEntity { RtId = page.RtId, CkTypeId = KnowledgePages.PageTypeId, RtWellKnownName = slug };
            update.SetAttributeValue(KnowledgePlatformUpdates.DismissedAttribute, AttributeValueTypesDto.Int, version);
            var failure = await KnowledgeTools.ApplyAsync(ctx.Repo, session, ctx.TenantId!,
                [EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(KnowledgePages.PageTypeId, page.RtId), update)],
                [], slug, page);
            return failure ?? new KnowledgeWriteResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Page = KnowledgePages.Summarize(ctx.TenantId!, page),
                Message = $"Dismissed Blueprint version {version} of '{slug}'; the page stays at version " +
                          $"{KnowledgePages.CurrentVersion(page)}."
            };
        }
        catch (Exception ex)
        {
            return new KnowledgeWriteResponse { TenantId = ctx.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Switches one unedited page to its target Blueprint version (one transaction per page).</summary>
    private static async Task<KnowledgePlatformAdoptionDto> AdoptAsync(McpServer server,
        KnowledgeTools.KnowledgeContext ctx, IOctoSession session, PlatformUpdate update, string runRef,
        CancellationToken ct)
    {
        var target = update.Target;
        var result = new KnowledgePlatformAdoptionDto
        {
            Slug = update.Slug,
            FromVersion = update.CurrentVersion,
            ToVersion = target.Number,
            Blueprint = target.Blueprint
        };

        // The content of the seeded version, its status included: a Blueprint version is reviewed with the product, and
        // the page it replaces is a Blueprint version as well (Decide only returns Adoptable then).
        var content = KnowledgePages.ReadContent(target.Entity);
        var refusal = await KnowledgeTools.GuardAsync(server, ctx, session, content, update.Slug,
            "knowledge_adopt_platform_updates", ct, new ContentGuardField("runRef", runRef));
        if (refusal != null)
        {
            result.Reason = $"{refusal.ErrorCode}: {refusal.ErrorMessage}";
            return result;
        }

        var page = new RtEntity { RtId = update.Page.RtId, CkTypeId = KnowledgePages.PageTypeId, RtWellKnownName = update.Slug };
        KnowledgePages.SetContent(page, content);
        // The hash of the knowledge tools (BuildWrite), so octo-cli's recomputation matches the adopted page.
        page.SetAttributeValue("ContentHash", AttributeValueTypesDto.String,
            KnowledgePages.Hash(KnowledgePages.Render(content, update.Slug, 0)));

        if (target.Entity.GetAttributeValueOrDefault("AuthoredAt") is DateTime authoredAt)
        {
            page.SetAttributeValue("CompiledAt", AttributeValueTypesDto.DateTime, authoredAt.ToUniversalTime());
        }

        page.SetAttributeValue("CurrentVersion", AttributeValueTypesDto.Int, target.Number);
        if (target.Blueprint != null)
        {
            page.SetAttributeValue("BlueprintVersion", AttributeValueTypesDto.String, target.Blueprint);
        }

        page.SetAttributeValue(KnowledgePlatformUpdates.AdoptedByRunAttribute, AttributeValueTypesDto.String, runRef);

        var associations = new List<AssociationUpdateInfo>();
        var declared = KnowledgePlatformUpdates.DeclaredLinks(target.Entity);
        if (declared != null)
        {
            await FollowLinksAsync(ctx.Repo, session, update.Page, declared, associations, result);
        }

        var failure = await KnowledgeTools.ApplyAsync(ctx.Repo, session, ctx.TenantId!,
            [EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(KnowledgePages.PageTypeId, update.Page.RtId), page)],
            associations, update.Slug, update.Page);
        if (failure != null)
        {
            result.Reason = $"{failure.ErrorCode ?? "WriteFailed"}: {failure.ErrorMessage}";
            return result;
        }

        result.IsSuccess = true;
        return result;
    }

    /// <summary>Makes the LinksTo associations of a page equal the declared slugs (unknown slugs are reported, not linked).</summary>
    private static async Task FollowLinksAsync(ITenantRepository repository, IOctoSession session, RtEntity page,
        IReadOnlyList<string> declared, List<AssociationUpdateInfo> associations, KnowledgePlatformAdoptionDto result)
    {
        var role = new RtCkId<CkAssociationRoleId>(KnowledgePages.LinksToRole);
        var origin = new RtEntityId(KnowledgePages.PageTypeId, page.RtId);
        var wanted = new HashSet<OctoObjectId>();
        foreach (var slug in declared.Where(s => s != page.RtWellKnownName))
        {
            var target = await KnowledgePages.FindBySlugAsync(repository, session, KnowledgePages.PageType, slug);
            if (target == null)
            {
                result.UnresolvedLinks.Add(slug);
                continue;
            }

            wanted.Add(target.RtId);
        }

        var linked = (await KnowledgePages.OutboundTargetsAsync(repository, session, page.RtId,
            KnowledgePages.LinksToRole)).ToHashSet();
        foreach (var id in wanted.Where(id => !linked.Contains(id)))
        {
            associations.Add(AssociationUpdateInfo.CreateInsert(origin, new RtEntityId(KnowledgePages.PageTypeId, id), role));
            result.LinksAdded++;
        }

        foreach (var id in linked.Where(id => !wanted.Contains(id)))
        {
            associations.Add(AssociationUpdateInfo.CreateDelete(origin, new RtEntityId(KnowledgePages.PageTypeId, id), role));
            result.LinksRemoved++;
        }
    }

    /// <summary>The adopt and dismiss writes need the System.Ai 4.10.0 page attributes.</summary>
    private static string? RequireModel(McpServer server, string tenantId)
    {
        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        return ckCache.TryGetRtCkType(tenantId, KnowledgePages.PageTypeId, out var pageType) && pageType != null &&
               pageType.AllAttributes.Values.Any(a => a.AttributeName == KnowledgePlatformUpdates.DismissedAttribute)
            ? null
            : $"Tenant '{tenantId}' cannot record platform updates of knowledge pages: it needs System.Ai 4.10.0 " +
              $"(KnowledgePage.{KnowledgePlatformUpdates.DismissedAttribute}, AB#8780).";
    }

    private static KnowledgePlatformUpdateDto Dto(PlatformUpdate u) => new()
    {
        Slug = u.Slug,
        State = u.State.ToString(),
        CurrentVersion = u.CurrentVersion,
        NewerVersion = u.Target.Number,
        Blueprint = u.Target.Blueprint,
        DismissedVersion = u.DismissedVersion
    };

    private static KnowledgeWriteResponse Invalid(string message) =>
        new() { ErrorCode = "InvalidArgument", ErrorMessage = message };
}
