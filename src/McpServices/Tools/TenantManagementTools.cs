using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tenant lifecycle tools backed by the Asset Repository service.
///     Mirrors the octo-cli commands <c>GetTenants</c>, <c>Create</c> (CreateTenant) and <c>Delete</c> (DeleteTenant).
/// </summary>
[McpServerToolType]
public sealed class TenantManagementTools
{
    /// <summary>List child tenants of the resolved parent tenant.</summary>
    [McpServerTool(Name = "get_tenants")]
    [Description(
        "List all child tenants visible from the given parent tenant. Equivalent to the octo-cli GetTenants " +
        "command. Returns each child's tenant ID and database name.")]
    public static async Task<GetTenantsResponse> GetTenants(
        McpServer server,
        [Description("Parent tenant whose child tenants should be listed. If omitted, the tenant is resolved from the URL route.")]
        string? tenantId = null)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new GetTenantsResponse
            {
                IsSuccess = false,
                ErrorMessage = "Not authenticated. Call 'authenticate' first."
            };
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var resolvedParent = tenantResolver.ResolveTenantId(tenantId);

            var clientFactory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var assetClient = clientFactory.CreateAssetClient(resolvedParent, accessToken);

            var tenants = await assetClient.GetTenantsAsync();
            var list = tenants
                .Select(t => new TenantInfoDto { TenantId = t.TenantId, Database = t.Database })
                .ToList();

            return new GetTenantsResponse
            {
                IsSuccess = true,
                ParentTenantId = resolvedParent,
                Tenants = list,
                TotalCount = list.Count,
                Message = list.Count == 0
                    ? $"No child tenants found under '{resolvedParent}'."
                    : $"Found {list.Count} child tenant(s) under '{resolvedParent}'."
            };
        }
        catch (Exception ex)
        {
            return new GetTenantsResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>Create a new child tenant under the resolved parent tenant.</summary>
    [McpServerTool(Name = "create_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Create a new child tenant under the given parent tenant. Equivalent to the octo-cli CreateTenant command. " +
        "Note: unlike the CLI, this does NOT automatically provision the calling user as admin — call the identity " +
        "provisioning tools separately once those are exposed.")]
    public static async Task<CreateTenantResponse> CreateTenant(
        McpServer server,
        [Description("Identifier of the new child tenant (will be lower-cased).")]
        string childTenantId,
        [Description("Name of the MongoDB database backing the new tenant (will be lower-cased).")]
        string database,
        [Description("Parent tenant under which the child is created. If omitted, the tenant is resolved from the URL route.")]
        string? tenantId = null)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new CreateTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = "Not authenticated. Call 'authenticate' first."
            };
        }

        if (string.IsNullOrWhiteSpace(childTenantId))
        {
            return new CreateTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = "childTenantId is required."
            };
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return new CreateTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = "database is required."
            };
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var resolvedParent = tenantResolver.ResolveTenantId(tenantId);

            var clientFactory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var assetClient = clientFactory.CreateAssetClient(resolvedParent, accessToken);

            var normalizedChild = childTenantId.ToLowerInvariant();
            var normalizedDb = database.ToLowerInvariant();

            await assetClient.CreateTenantAsync(normalizedChild, normalizedDb);

            return new CreateTenantResponse
            {
                IsSuccess = true,
                ParentTenantId = resolvedParent,
                CreatedTenantId = normalizedChild,
                Database = normalizedDb,
                Message = $"Tenant '{normalizedChild}' (database '{normalizedDb}') created under '{resolvedParent}'."
            };
        }
        catch (Exception ex)
        {
            return new CreateTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>Delete a child tenant. Destructive: requires <paramref name="confirm"/> to be true.</summary>
    [McpServerTool(Name = "delete_tenant")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Delete a child tenant. DESTRUCTIVE — requires confirm=true. Equivalent to the octo-cli DeleteTenant " +
        "command without the interactive confirmation prompt.")]
    public static async Task<DeleteTenantResponse> DeleteTenant(
        McpServer server,
        [Description("Identifier of the child tenant to delete.")]
        string childTenantId,
        [Description("Must be set to true to actually perform the deletion. Without this, the call is rejected.")]
        bool confirm = false,
        [Description("Parent tenant under which the child lives. If omitted, the tenant is resolved from the URL route.")]
        string? tenantId = null)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new DeleteTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = "Not authenticated. Call 'authenticate' first."
            };
        }

        if (string.IsNullOrWhiteSpace(childTenantId))
        {
            return new DeleteTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = "childTenantId is required."
            };
        }

        if (!confirm)
        {
            return new DeleteTenantResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to delete tenant '{childTenantId}' without explicit confirmation. " +
                    "Call again with confirm=true to proceed."
            };
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var resolvedParent = tenantResolver.ResolveTenantId(tenantId);

            var clientFactory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var assetClient = clientFactory.CreateAssetClient(resolvedParent, accessToken);

            await assetClient.DeleteTenantAsync(childTenantId);

            return new DeleteTenantResponse
            {
                IsSuccess = true,
                ParentTenantId = resolvedParent,
                DeletedTenantId = childTenantId,
                Message = $"Tenant '{childTenantId}' deleted from '{resolvedParent}'."
            };
        }
        catch (Exception ex)
        {
            return new DeleteTenantResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>Reset a child tenant to factory defaults. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "clean_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Reset a child tenant to its initial state, deleting all data and resetting the construction kit to " +
        "system-only models. DESTRUCTIVE — requires confirm=true.")]
    public static Task<TenantOperationResponse> CleanTenant(
        McpServer server,
        [Description("Identifier of the child tenant to clean.")] string childTenantId,
        [Description("Must be true to actually clean. Without this, the call is rejected.")] bool confirm = false,
        [Description("Parent tenant context. Falls back to URL route.")] string? tenantId = null)
        => InvokeAsync(server, tenantId, childTenantId, confirm,
            requiredConfirm: true,
            confirmHint: "Refusing to clean tenant without confirm=true.",
            (client, child) => client.CleanTenantAsync(child),
            successMessage: child => $"Tenant '{child}' cleaned (reset to factory defaults).");

    /// <summary>Attach an existing database as a child tenant.</summary>
    [McpServerTool(Name = "attach_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Attach an existing database as a child tenant. The database must already exist; only the metadata " +
        "binding is created. Non-destructive.")]
    public static async Task<TenantOperationResponse> AttachTenant(
        McpServer server,
        [Description("Identifier of the child tenant to attach.")] string childTenantId,
        [Description("Name of the existing database to attach.")] string database,
        [Description("Parent tenant context. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await TryBuildContext(server, tenantId);
        if (ctx.Error != null)
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        if (string.IsNullOrWhiteSpace(childTenantId) || string.IsNullOrWhiteSpace(database))
        {
            return new TenantOperationResponse
            {
                IsSuccess = false,
                ErrorMessage = "childTenantId and database are required."
            };
        }

        try
        {
            await ctx.Client!.AttachTenantAsync(childTenantId.ToLowerInvariant(), database.ToLowerInvariant());
            return new TenantOperationResponse
            {
                IsSuccess = true,
                ChildTenantId = childTenantId,
                ParentTenantId = ctx.ParentTenantId,
                Message = $"Tenant '{childTenantId}' attached to database '{database}' under '{ctx.ParentTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Detach a child tenant without deleting its database.</summary>
    [McpServerTool(Name = "detach_tenant")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Detach a child tenant: removes the metadata binding but leaves the database intact. Non-destructive at " +
        "the data level; the tenant becomes invisible until re-attached.")]
    public static Task<TenantOperationResponse> DetachTenant(
        McpServer server,
        [Description("Identifier of the child tenant to detach.")] string childTenantId,
        [Description("Parent tenant context. Falls back to URL route.")] string? tenantId = null)
        => InvokeAsync(server, tenantId, childTenantId, confirm: true,
            requiredConfirm: false,
            confirmHint: null,
            (client, child) => client.DetachTenantAsync(child),
            successMessage: child => $"Tenant '{child}' detached (database preserved).");

    /// <summary>Clear the cache of a child tenant. Destructive in performance terms; requires confirm.</summary>
    [McpServerTool(Name = "clear_tenant_cache")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Clear the in-memory caches of a child tenant. May cause temporary performance degradation while caches " +
        "re-warm. Requires confirm=true.")]
    public static Task<TenantOperationResponse> ClearTenantCache(
        McpServer server,
        [Description("Identifier of the child tenant whose cache should be cleared.")] string childTenantId,
        [Description("Must be true to actually clear. Without this, the call is rejected.")] bool confirm = false,
        [Description("Parent tenant context. Falls back to URL route.")] string? tenantId = null)
        => InvokeAsync(server, tenantId, childTenantId, confirm,
            requiredConfirm: true,
            confirmHint: "Refusing to clear tenant cache without confirm=true.",
            (client, child) => client.ClearTenantCacheAsync(child),
            successMessage: child => $"Cache cleared for tenant '{child}'.");

    /// <summary>Update the system construction-kit model of a child tenant.</summary>
    [McpServerTool(Name = "update_system_ck_model")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Update the system construction-kit model of a child tenant to the currently-published version. " +
        "Non-destructive but may affect entity validation behaviour.")]
    public static Task<TenantOperationResponse> UpdateSystemCkModel(
        McpServer server,
        [Description("Identifier of the child tenant to update.")] string childTenantId,
        [Description("Parent tenant context. Falls back to URL route.")] string? tenantId = null)
        => InvokeAsync(server, tenantId, childTenantId, confirm: true,
            requiredConfirm: false,
            confirmHint: null,
            (client, child) => client.UpdateSystemCkModelOfTenant(child),
            successMessage: child => $"System CK model updated for tenant '{child}'.");

    private record AssetClientContext(
        global::Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System.IAssetServicesClient? Client,
        string? ParentTenantId,
        string? Error);

    private static async Task<AssetClientContext> TryBuildContext(McpServer server, string? tenantId)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new AssetClientContext(null, null,
                "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var parent = tenantResolver.ResolveTenantId(tenantId);
            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new AssetClientContext(factory.CreateAssetClient(parent, accessToken), parent, null);
        }
        catch (Exception ex)
        {
            return new AssetClientContext(null, null, ex.Message);
        }
    }

    private static async Task<TenantOperationResponse> InvokeAsync(
        McpServer server,
        string? tenantId,
        string childTenantId,
        bool confirm,
        bool requiredConfirm,
        string? confirmHint,
        Func<global::Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System.IAssetServicesClient, string, Task> action,
        Func<string, string> successMessage)
    {
        if (string.IsNullOrWhiteSpace(childTenantId))
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = "childTenantId is required." };
        }

        if (requiredConfirm && !confirm)
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = confirmHint };
        }

        var ctx = await TryBuildContext(server, tenantId);
        if (ctx.Error != null)
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await action(ctx.Client!, childTenantId);
            return new TenantOperationResponse
            {
                IsSuccess = true,
                ChildTenantId = childTenantId,
                ParentTenantId = ctx.ParentTenantId,
                Message = successMessage(childTenantId)
            };
        }
        catch (Exception ex)
        {
            return new TenantOperationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
