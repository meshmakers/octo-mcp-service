namespace Meshmakers.Octo.Backend.McpServices.Options;

/// <summary>
///     Endpoint URLs for the OctoMesh backend services that the MCP server proxies via the SDK service clients.
///     Bound from configuration section <c>OctoServiceUrls</c>.
/// </summary>
public class OctoServiceUrlOptions
{
    /// <summary>
    ///     Base URL of the Asset Repository service (used for tenant CRUD, blueprints, CK model libraries, etc.).
    /// </summary>
    public string? AssetServiceUrl { get; set; }

    /// <summary>
    ///     Base URL of the Identity service (users, roles, clients, groups, identity providers).
    /// </summary>
    public string? IdentityServiceUrl { get; set; }

    /// <summary>
    ///     Base URL of the Communication Controller service (adapters, pipelines, workloads).
    /// </summary>
    public string? CommunicationServiceUrl { get; set; }

    /// <summary>
    ///     Base URL of the Bot service (notifications).
    /// </summary>
    public string? BotServiceUrl { get; set; }

    /// <summary>
    ///     Base URL of the Reporting service.
    /// </summary>
    public string? ReportingServiceUrl { get; set; }

    /// <summary>
    ///     Base URL of the AI service (AI Services capability of a tenant: enable_ai / disable_ai, and the AI part of
    ///     delete_tenant disableFeatures).
    /// </summary>
    public string? AiServiceUrl { get; set; }
}
