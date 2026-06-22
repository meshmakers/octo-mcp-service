using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     OAuth API secret management tools. Mirrors octo-cli ApiSecrets commands which exist in two parallel
///     variants — one for client secrets, one for API-resource secrets. Each variant has Get(list) / Create /
///     Update / Delete.
/// </summary>
[McpServerToolType]
public sealed class ApiSecretTools
{
    // ── Client secrets ──────────────────────────────────────────────────────

    /// <summary>List secrets attached to a client.</summary>
    [McpServerTool(Name = "get_client_secrets")]
    [Description("List the API secrets attached to a client. Equivalent to octo-cli GetApiSecretsClient.")]
    public static async Task<GetApiSecretsResponse> GetClientSecrets(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = "clientId is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var secrets = (await ctx.Client!.GetApiSecretsForClient(clientId)).ToList();
            return new GetApiSecretsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = clientId,
                ApiSecrets = secrets,
                TotalCount = secrets.Count,
                Message = secrets.Count == 0
                    ? $"No secrets on client '{clientId}'."
                    : $"{secrets.Count} secret(s) on client '{clientId}'."
            };
        }
        catch (Exception ex)
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new secret for a client.</summary>
    [McpServerTool(Name = "create_client_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Create a new API secret on a client. Equivalent to octo-cli CreateApiSecretClient. The server returns " +
        "the encrypted value; capture it from the response.")]
    public static async Task<ApiSecretResponse> CreateClientSecret(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Cleartext secret value.")] string valueClearText,
        [Description("Optional expiration date (UTC).")] DateTime? expirationDate = null,
        [Description("Optional description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(valueClearText))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and valueClearText are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.CreateApiSecretForClient(clientId, new ApiSecretDto
            {
                ValueClearText = valueClearText,
                ExpirationDate = expirationDate,
                Description = description
            });
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = clientId,
                SecretValue = result.ValueEncrypted,
                Message = $"Secret created on client '{clientId}'."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update a client secret.</summary>
    [McpServerTool(Name = "update_client_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Update a client secret. Equivalent to octo-cli UpdateApiSecretClient.")]
    public static async Task<ApiSecretResponse> UpdateClientSecret(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Existing encrypted value (identifies the secret).")] string valueEncrypted,
        [Description("Optional new expiration date (UTC).")] DateTime? expirationDate = null,
        [Description("Optional new description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(valueEncrypted))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and valueEncrypted are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateApiSecretClient(clientId, new ApiSecretDto
            {
                ValueEncrypted = valueEncrypted,
                ExpirationDate = expirationDate,
                Description = description
            });
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = clientId,
                SecretValue = valueEncrypted,
                Message = $"Secret on client '{clientId}' updated."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a client secret. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_client_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Delete a client secret. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteApiSecretClient.")]
    public static async Task<ApiSecretResponse> DeleteClientSecret(
        McpServer server,
        [Description("Client ID.")] string clientId,
        [Description("Encrypted value (identifies the secret).")] string valueEncrypted,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(valueEncrypted))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "clientId and valueEncrypted are required."
            };
        }

        if (!confirm)
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete secret on client '{clientId}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteApiSecretClient(clientId, valueEncrypted);
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = clientId,
                SecretValue = valueEncrypted,
                Message = $"Secret on client '{clientId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    // ── API-resource secrets ────────────────────────────────────────────────

    /// <summary>List secrets attached to an API resource.</summary>
    [McpServerTool(Name = "get_api_resource_secrets")]
    [Description(
        "List the API secrets attached to an API resource. Equivalent to octo-cli GetApiSecretsApiResource.")]
    public static async Task<GetApiSecretsResponse> GetApiResourceSecrets(
        McpServer server,
        [Description("API resource name.")] string apiResourceName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(apiResourceName))
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = "apiResourceName is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var secrets = (await ctx.Client!.GetApiSecretsForApiResource(apiResourceName)).ToList();
            return new GetApiSecretsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = apiResourceName,
                ApiSecrets = secrets,
                TotalCount = secrets.Count
            };
        }
        catch (Exception ex)
        {
            return new GetApiSecretsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new secret for an API resource.</summary>
    [McpServerTool(Name = "create_api_resource_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Create a new API secret on an API resource. Equivalent to octo-cli CreateApiSecretApiResource.")]
    public static async Task<ApiSecretResponse> CreateApiResourceSecret(
        McpServer server,
        [Description("API resource name.")] string apiResourceName,
        [Description("Cleartext secret value.")] string valueClearText,
        [Description("Optional expiration date (UTC).")] DateTime? expirationDate = null,
        [Description("Optional description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(apiResourceName) || string.IsNullOrWhiteSpace(valueClearText))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "apiResourceName and valueClearText are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.CreateApiSecretForApiResource(apiResourceName, new ApiSecretDto
            {
                ValueClearText = valueClearText,
                ExpirationDate = expirationDate,
                Description = description
            });
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = apiResourceName,
                SecretValue = result.ValueEncrypted,
                Message = $"Secret created on API resource '{apiResourceName}'."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update an API resource secret.</summary>
    [McpServerTool(Name = "update_api_resource_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Update an API resource secret. Equivalent to octo-cli UpdateApiSecretApiResource.")]
    public static async Task<ApiSecretResponse> UpdateApiResourceSecret(
        McpServer server,
        [Description("API resource name.")] string apiResourceName,
        [Description("Existing encrypted value (identifies the secret).")] string valueEncrypted,
        [Description("Optional new expiration date (UTC).")] DateTime? expirationDate = null,
        [Description("Optional new description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(apiResourceName) || string.IsNullOrWhiteSpace(valueEncrypted))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "apiResourceName and valueEncrypted are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateApiSecretApiResource(apiResourceName, new ApiSecretDto
            {
                ValueEncrypted = valueEncrypted,
                ExpirationDate = expirationDate,
                Description = description
            });
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = apiResourceName,
                SecretValue = valueEncrypted,
                Message = $"Secret on API resource '{apiResourceName}' updated."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an API resource secret. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_api_resource_secret")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Delete an API resource secret. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteApiSecretApiResource.")]
    public static async Task<ApiSecretResponse> DeleteApiResourceSecret(
        McpServer server,
        [Description("API resource name.")] string apiResourceName,
        [Description("Encrypted value (identifies the secret).")] string valueEncrypted,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(apiResourceName) || string.IsNullOrWhiteSpace(valueEncrypted))
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage = "apiResourceName and valueEncrypted are required."
            };
        }

        if (!confirm)
        {
            return new ApiSecretResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to delete secret on API resource '{apiResourceName}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteApiSecretApiResource(apiResourceName, valueEncrypted);
            return new ApiSecretResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                OwnerId = apiResourceName,
                SecretValue = valueEncrypted,
                Message = $"Secret on API resource '{apiResourceName}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ApiSecretResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
