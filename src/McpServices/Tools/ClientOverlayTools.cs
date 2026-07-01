using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Client overlay-URI tools (Identity service, AB#4209). Mirror the octo-cli
///     <c>ApplyClientOverlay</c> / <c>CleanClientOverlays</c> commands: append operator-scoped
///     overlay URIs (RedirectUris / PostLogoutRedirectUris / AllowedCorsOrigins) onto a
///     blueprint-managed client without touching the blueprint, and strip them again before a
///     sanitised tenant dump. Overlay entries carry <c>Source = "overlay:&lt;name&gt;"</c> and
///     survive blueprint re-apply via the Step 2a preservation pass.
/// </summary>
[McpServerToolType]
public sealed class ClientOverlayTools
{
    /// <summary>Append overlay URIs to a blueprint-managed client.</summary>
    [McpServerTool(Name = "apply_client_overlay")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Append operator-scoped overlay URIs to a blueprint-managed OAuth client. Equivalent to octo-cli " +
        "ApplyClientOverlay. New entries are written with Source = 'overlay:<overlayName>' and survive blueprint " +
        "re-apply. Idempotent — URIs already present (any source) are skipped. At least one of redirectUris / " +
        "postLogoutRedirectUris / allowedCorsOrigins must be non-empty. Pass CORS origins without a trailing slash.")]
    public static async Task<ApplyClientOverlayResponse> ApplyClientOverlay(
        McpServer server,
        [Description("The ClientId to apply the overlay to. Must already exist.")] string clientId,
        [Description("Operator-meaningful overlay name. Becomes the 'overlay:<name>' suffix. Constrained to [A-Za-z0-9._-]+.")] string overlayName,
        [Description("Redirect URIs to add. Existing duplicates (any source) are skipped.")] List<string>? redirectUris = null,
        [Description("Post-logout redirect URIs to add. Existing duplicates are skipped.")] List<string>? postLogoutRedirectUris = null,
        [Description("CORS origins to add. Pass without trailing slash. Existing duplicates are skipped.")] List<string>? allowedCorsOrigins = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(overlayName))
        {
            return new ApplyClientOverlayResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and overlayName are required."
            };
        }

        var redirect = NonEmptyOrNull(redirectUris);
        var postLogout = NonEmptyOrNull(postLogoutRedirectUris);
        var cors = NonEmptyOrNull(allowedCorsOrigins);

        if (redirect == null && postLogout == null && cors == null)
        {
            return new ApplyClientOverlayResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    "At least one of redirectUris / postLogoutRedirectUris / allowedCorsOrigins must contain a URI."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApplyClientOverlayResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = new ApplyOverlayUrisDto
            {
                OverlayName = overlayName,
                RedirectUris = redirect,
                PostLogoutRedirectUris = postLogout,
                AllowedCorsOrigins = cors
            };

            var result = await ctx.Client!.ApplyClientOverlay(clientId, dto);

            return new ApplyClientOverlayResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ClientId = result.ClientId,
                OverlayName = result.OverlayName,
                Result = result,
                Message =
                    $"Overlay '{result.OverlayName}' on '{result.ClientId}': " +
                    $"RedirectUris +{result.RedirectUris.Added}/~{result.RedirectUris.SkippedDuplicate}, " +
                    $"PostLogoutRedirectUris +{result.PostLogoutRedirectUris.Added}/~{result.PostLogoutRedirectUris.SkippedDuplicate}, " +
                    $"AllowedCorsOrigins +{result.AllowedCorsOrigins.Added}/~{result.AllowedCorsOrigins.SkippedDuplicate}."
            };
        }
        catch (Exception ex)
        {
            return new ApplyClientOverlayResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Strip overlay URIs from every blueprint-managed client. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "clean_client_overlays")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Strip overlay URI entries from every blueprint-managed client in the tenant. Equivalent to octo-cli " +
        "CleanClientOverlays. DESTRUCTIVE — requires confirm=true. Without overlayName every Source matching " +
        "'overlay:*' is removed; with overlayName only 'overlay:<name>' entries are removed. base / api entries " +
        "are always preserved. Typical use is before a sanitised dump_tenant export.")]
    public static async Task<CleanClientOverlaysResponse> CleanClientOverlays(
        McpServer server,
        [Description("Must be true to actually strip overlay entries.")] bool confirm = false,
        [Description("Optional overlay name. Without it, every 'overlay:*' source is removed; with it, only 'overlay:<name>'.")] string? overlayName = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            var subject = string.IsNullOrWhiteSpace(overlayName)
                ? "ALL overlay:* URI entries on every client"
                : $"every overlay:{overlayName} URI entry on every client";
            return new CleanClientOverlaysResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to strip {subject} without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CleanClientOverlaysResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var filter = string.IsNullOrWhiteSpace(overlayName) ? null : overlayName;
            var result = await ctx.Client!.CleanOverlayEntries(filter);

            return new CleanClientOverlaysResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OverlayName = result.OverlayName,
                Result = result,
                Message =
                    $"Clean complete: {result.TotalEntriesRemoved} entr(y/ies) removed across " +
                    $"{result.ClientsAffected} client(s)."
            };
        }
        catch (Exception ex)
        {
            return new CleanClientOverlaysResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static List<string>? NonEmptyOrNull(List<string>? input)
    {
        if (input == null)
        {
            return null;
        }

        var cleaned = input
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .ToList();

        return cleaned.Count == 0 ? null : cleaned;
    }
}
