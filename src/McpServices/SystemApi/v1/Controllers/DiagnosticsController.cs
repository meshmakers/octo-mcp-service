using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.McpServices.SystemApi.v1.Controllers;

/// <summary>
///     Manages the diagnostics settings of the service
/// </summary>
/// <remarks>
///     🔴 AB#5059 — the <c>[Authorize]</c> attributes on this controller were <b>commented out</b>, so
///     <c>POST system/v1/diagnostics/reconfigureLogLevel</c> was anonymous: anyone who could reach the
///     pod could reconfigure the log level of the whole process. Turning every logger to Trace on a
///     server that brokers tenant data is both a denial-of-service lever (disk and log-pipeline cost)
///     and a disclosure one — and turning them off hides an attack in progress. Every sibling service
///     gates the identical endpoint (asset-repo, bot, communication-controller, identity).
///     <para>
///         The policy is <see cref="McpAuthorizationPolicy.PolicyName" />, the one this service already
///         requires on both <c>MapMcp</c> transport endpoints — deliberately not a new policy of this
///         controller's own. It is <c>RequireAuthenticatedUser</c> plus at least one of
///         <c>Mcp:RequiredApiScopes</c> (default <c>octo_api</c>), which is the same platform write
///         scope the sibling services' system diagnostics endpoints require. A caller that can already
///         invoke <c>delete_tenant</c> over the transport therefore gains nothing new here, and a token
///         without the platform scope reaches neither surface.
///     </para>
///     <para>
///         The scheme is pinned to JWT bearer, matching the sibling controllers and this host's
///         <c>DefaultAuthenticateScheme</c>. Without it the default <i>challenge</i> scheme is the MCP
///         one, which answers an unauthenticated request with the RFC 9728 protected-resource metadata
///         intended for MCP clients — a confusing answer for a plain REST endpoint.
///     </para>
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DiagnosticsController : ControllerBase
{
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly ILogger<DiagnosticsController> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="diagnosticsService"></param>
    public DiagnosticsController(ILogger<DiagnosticsController> logger, IDiagnosticsService diagnosticsService)
    {
        _logger = logger;
        _diagnosticsService = diagnosticsService;
    }

    /// <summary>
    ///     Reconfigures the log level of the service
    /// </summary>
    /// <param name="minLogLevel">The minimal log level to be logged.</param>
    /// <param name="maxLogLevel">The maximal log level to be logged.</param>
    /// <param name="loggerName">The name of the logger to be reconfigured.</param>
    /// <returns></returns>
    [HttpPost("reconfigureLogLevel")]
    [Authorize(McpAuthorizationPolicy.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReconfigureLogLevelAsync([Required] LogLevelDto minLogLevel,
        [Required] LogLevelDto maxLogLevel, string loggerName = "*")
    {
        try
        {
            _logger.LogInformation(
                "Reconfiguring logger {LoggerName} log level to min level {MinLogLevel}, max level {MaxLoglevel}",
                loggerName, minLogLevel, maxLogLevel);
            await _diagnosticsService.ReconfigureLogLevelAsync(minLogLevel, maxLogLevel, loggerName);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}