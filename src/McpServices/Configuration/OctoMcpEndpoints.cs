using Meshmakers.Octo.Services.Infrastructure.Authorization;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     Maps the two MCP endpoints of this service.
/// </summary>
internal static class OctoMcpEndpoints
{
    /// <summary>The MCP endpoint with tenant routing.</summary>
    public const string TenantRoute = "/{tenantId:tenantId}/mcp";

    /// <summary>The tenantless MCP endpoint (tenant resolved via tool parameter).</summary>
    public const string TenantlessRoute = "/mcp";

    /// <summary>
    ///     Maps <see cref="TenantRoute" /> and <see cref="TenantlessRoute" /> with
    ///     <see cref="McpAuthorizationPolicy.EndpointPolicyName" /> (AB#5032: the scope requirement on top of
    ///     "authenticated"; AB#9729: an agent client token also passes) and <c>.AllowAgentTokens()</c>.
    /// </summary>
    /// <remarks>
    ///     AB#6054: these two endpoints are the only OctoMesh endpoints that accept agent tokens (scope
    ///     <c>octo_agent</c>); <c>UseOctoAgentTokenRefusal()</c> before <c>UseAuthorization()</c> (and
    ///     <c>UseOctoTenantAuthorization()</c> after it) refuses them everywhere else, the controllers of this service
    ///     included, with <c>403 AgentTokenNotAccepted</c>. Here the mandate guard checks every call, and every
    ///     downstream call uses the token MCP obtains by exchanging the agent token. Never mark another endpoint.
    /// </remarks>
    /// <param name="app">The endpoint route builder.</param>
    public static IEndpointRouteBuilder MapOctoMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMcp(TenantRoute)
            .RequireAuthorization(McpAuthorizationPolicy.EndpointPolicyName)
            .AllowAgentTokens();

        app.MapMcp(TenantlessRoute)
            .RequireAuthorization(McpAuthorizationPolicy.EndpointPolicyName)
            .AllowAgentTokens();
        return app;
    }
}
