using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Runtime log-level reconfiguration. Mirrors the octo-cli ReconfigureLogLevel command which dispatches to
///     the matching service client based on a name parameter. Supports the five backend services that the CLI
///     supports: Identity, AssetRepository, Communication Controller, Reporting, Bot.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticsTools
{
    /// <summary>Reconfigure the log level of a backend service at runtime.</summary>
    [McpServerTool(Name = "reconfigure_log_level")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Reconfigure the log level of a backend service at runtime. The serviceName selects which service " +
        "receives the request: 'Identity', 'AssetRepository', 'Communication', 'Reporting', or 'Bot'. " +
        "loggerName is a logger pattern such as 'Microsoft.*' or '*'. logLevel values: Trace, " +
        "Debug, Info, Warn, Error, Fatal, Off. Equivalent to octo-cli ReconfigureLogLevel.")]
    public static async Task<TimeSeriesResponse> ReconfigureLogLevel(
        McpServer server,
        [Description("Service name: 'Identity', 'AssetRepository', 'Communication', 'Reporting', or 'Bot'.")]
        string serviceName,
        [Description("Logger pattern, e.g. 'Meshmakers.*', '*'.")] string loggerName,
        [Description("Minimum log level (Trace, Debug, Info, Warn, Error, Fatal, Off).")] LogLevelDto minLogLevel,
        [Description("Maximum log level (Trace, Debug, Info, Warn, Error, Fatal, Off).")] LogLevelDto maxLogLevel,
        [Description("Tenant to operate on. Falls back to URL route. Used only for routing the tenant-scoped clients.")]
        string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(loggerName))
        {
            return new TimeSeriesResponse
            {
                IsSuccess = false,
                ErrorMessage = "serviceName and loggerName are required."
            };
        }

        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new TimeSeriesResponse
            {
                IsSuccess = false,
                ErrorMessage = Constants.NotAuthenticatedError
            };
        }

        try
        {
            switch (serviceName.ToLowerInvariant())
            {
                case "identity":
                {
                    var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
                    if (ctx.Error != null) return Error(ctx.Error);
                    await ctx.Client!.ReconfigureLogLevelAsync(loggerName, minLogLevel, maxLogLevel);
                    return Ok(ctx.TenantId, "Identity", loggerName, minLogLevel, maxLogLevel);
                }
                case "assetrepository":
                case "asset":
                {
                    var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
                    if (ctx.Error != null) return Error(ctx.Error);
                    await ctx.Client!.ReconfigureLogLevelAsync(loggerName, minLogLevel, maxLogLevel);
                    return Ok(ctx.TenantId, "AssetRepository", loggerName, minLogLevel, maxLogLevel);
                }
                case "communicationcontroller":
                case "communication":
                {
                    var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
                    if (ctx.Error != null) return Error(ctx.Error);
                    await ctx.Client!.ReconfigureLogLevelAsync(loggerName, minLogLevel, maxLogLevel);
                    return Ok(ctx.TenantId, "Communication", loggerName, minLogLevel, maxLogLevel);
                }
                case "reporting":
                {
                    var ctx = await ReportingClientContext.TryBuildAsync(server, tenantId);
                    if (ctx.Error != null) return Error(ctx.Error);
                    await ctx.Client!.ReconfigureLogLevelAsync(loggerName, minLogLevel, maxLogLevel);
                    return Ok(ctx.TenantId, "Reporting", loggerName, minLogLevel, maxLogLevel);
                }
                case "bot":
                {
                    var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
                    var botClient = factory.CreateBotClient(accessToken);
                    await botClient.ReconfigureLogLevelAsync(loggerName, minLogLevel, maxLogLevel);
                    return Ok(tenantId: null, "Bot", loggerName, minLogLevel, maxLogLevel);
                }
                default:
                    return Error(
                        $"Unknown serviceName '{serviceName}'. Allowed: Identity, AssetRepository, " +
                        "Communication, Reporting, Bot.");
            }
        }
        catch (Exception ex)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }

        static TimeSeriesResponse Error(string message) =>
            new() { IsSuccess = false, ErrorMessage = message };

        static TimeSeriesResponse Ok(string? tenantId, string svc, string logger,
            LogLevelDto min, LogLevelDto max) =>
            new()
            {
                IsSuccess = true,
                TenantId = tenantId,
                Message = $"Log level for '{logger}' on '{svc}' set to [{min}..{max}]."
            };
    }
}
