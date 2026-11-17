using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     AB#9188: the HTTP-gate half of the trusted AI service client, registered in one place so <c>Program.cs</c> and the
///     tests use the same wiring.
/// </summary>
internal static class AiServiceClientTenantTrustExtensions
{
    /// <summary>
    ///     Adds <c>Mcp:AiServiceClientId</c> to <c>TenantAuthorization:CrossTenantServiceClientIds</c>
    ///     (<see cref="ConfigureTenantAuthorizationOptions" />) and builds the options at startup, so a refused value
    ///     (<c>*</c> pattern) stops the start instead of failing the first request. Call after
    ///     <c>AddOctoTenantAuthorization</c>.
    /// </summary>
    public static IServiceCollection AddAiServiceClientTenantTrust(this IServiceCollection services)
    {
        services.AddSingleton<IPostConfigureOptions<TenantAuthorizationOptions>, ConfigureTenantAuthorizationOptions>();
        services.AddOptions<TenantAuthorizationOptions>().ValidateOnStart();
        return services;
    }

    /// <summary>
    ///     Adds <see cref="AiServiceClientTenantGuard" />. Call directly before <c>UseOctoTenantAuthorization()</c>.
    /// </summary>
    public static IApplicationBuilder UseAiServiceClientTenantGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<AiServiceClientTenantGuard>();
}
