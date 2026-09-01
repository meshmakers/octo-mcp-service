using System.Linq;
using System.Reflection;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.SystemApi.v1.Controllers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
/// AB#5059 — <c>POST system/v1/diagnostics/reconfigureLogLevel</c> shipped with both of its
/// <c>[Authorize]</c> attributes <b>commented out</b>, so anyone who could reach the pod could
/// reconfigure NLog for the whole process: turn every logger to Trace (disk and log-pipeline
/// denial-of-service, plus disclosure on a server that brokers tenant data) or silence them all.
/// Every sibling service gates the identical endpoint.
/// <para>
/// The requirement is <see cref="McpAuthorizationPolicy.PolicyName" /> — the policy this service
/// already applies to both <c>MapMcp</c> transport endpoints — rather than a policy invented for this
/// controller. Two things make this a real test rather than a tautology: the attributes are the only
/// gate on a controller reached through <c>MapControllerRoute</c> (no endpoint-level
/// <c>RequireAuthorization</c> exists for it), and the name they reference must be a policy that is
/// actually registered. <c>Constants.SystemApiPolicy</c>, which the commented-out code named, is
/// registered nowhere — uncommenting it verbatim would have thrown
/// <c>InvalidOperationException: The AuthorizationPolicy named: 'SystemApiPolicy' was not found</c>
/// on every request.
/// </para>
/// </summary>
public class DiagnosticsControllerAuthorizationTests
{
    private static MethodInfo ReconfigureLogLevel =>
        typeof(DiagnosticsController).GetMethod(nameof(DiagnosticsController.ReconfigureLogLevelAsync))!;

    [Fact]
    public void Controller_RequiresAuthentication_OnTheBearerScheme()
    {
        var attribute = typeof(DiagnosticsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();

        attribute.Should().NotBeNull("the controller must not be anonymous");
        attribute!.AuthenticationSchemes.Should().Be(JwtBearerDefaults.AuthenticationScheme,
            "the default challenge scheme of this host is the MCP one, which answers with RFC 9728 " +
            "protected-resource metadata — the wrong answer for a plain REST endpoint");
    }

    [Fact]
    public void ReconfigureLogLevel_RequiresTheTransportScopePolicy()
    {
        var policies = ReconfigureLogLevel
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Select(a => a.Policy)
            .ToArray();

        policies.Should().Contain(McpAuthorizationPolicy.PolicyName);
    }

    /// <summary>
    /// The policy name has to be one <c>Program.cs</c> registers, otherwise the attribute throws at
    /// request time instead of authorizing. <see cref="McpAuthorizationPolicy.AddMcpTransportPolicy" />
    /// is the single registration point, so asserting the constant matches what it registers is the
    /// check that the two cannot drift.
    /// </summary>
    [Fact]
    public void TheReferencedPolicy_IsTheOneRegisteredForTheTransport()
    {
        var options = new AuthorizationOptions();
        McpAuthorizationPolicy.AddMcpTransportPolicy(options, ["octo_api"]);

        options.GetPolicy(McpAuthorizationPolicy.PolicyName).Should().NotBeNull();
    }
}
