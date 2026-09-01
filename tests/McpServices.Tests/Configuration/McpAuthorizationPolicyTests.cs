using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
///     AB#5032 — the MCP transport endpoints must require a platform API scope, not merely a valid
///     token. Both encodings of the <c>scope</c> claim are accepted (one claim per scope, and the raw
///     space-delimited form), and the default requirement is the write scope <c>octo_api</c> because a
///     single JSON-RPC endpoint carries read and write tools alike.
/// </summary>
public class McpAuthorizationPolicyTests
{
    private static readonly string[] DefaultRequired = [CommonConstants.OctoApiFullAccess];

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer"));

    private static Claim Scope(string value) => new(InfrastructureCommon.ClaimScope, value);

    // ── The pure scope check ────────────────────────────────────────────────

    [Fact]
    public void HasRequiredScope_FullAccessScope_Accepted()
    {
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope(CommonConstants.OctoApiFullAccess)), DefaultRequired)
            .Should().BeTrue();
    }

    [Fact]
    public void HasRequiredScope_SpaceDelimitedClaim_Accepted()
    {
        // Some handlers leave the raw OAuth "scope" string intact instead of splitting it into one
        // claim per scope; a correctly-scoped token must not be refused because of that.
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope("openid profile octo_api offline_access")), DefaultRequired)
            .Should().BeTrue();
    }

    [Fact]
    public void HasRequiredScope_ReadOnlyScopeOnly_Refused()
    {
        // Deliberate: the endpoint cannot tell a read tool from a write tool (the tool name is in the
        // JSON-RPC body, authorization runs before it is read), so accepting the read-only scope would
        // hand it the whole write surface.
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope(CommonConstants.OctoApiReadOnly)), DefaultRequired)
            .Should().BeFalse();
    }

    [Fact]
    public void HasRequiredScope_NoApiScopeAtAll_Refused()
    {
        // The pre-AB#5032 hole: a front-end token with only openid/profile passed RequireAuthorization().
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope("openid"), Scope("profile")), DefaultRequired)
            .Should().BeFalse();
    }

    [Fact]
    public void HasRequiredScope_ScopeAsSubstringOfAnother_Refused()
    {
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope("octo_api_evil")), DefaultRequired)
            .Should().BeFalse();
    }

    [Fact]
    public void HasRequiredScope_NoPrincipal_Refused()
    {
        McpAuthorizationPolicy.HasRequiredScope(null, DefaultRequired).Should().BeFalse();
    }

    [Fact]
    public void HasRequiredScope_WidenedRequirement_AcceptsTheAddedScope()
    {
        // The set is configurable so an operator can admit a service account provisioned with a
        // different scope without a code change.
        McpAuthorizationPolicy
            .HasRequiredScope(Principal(Scope(CommonConstants.OctoApiReadOnly)),
                [CommonConstants.OctoApiFullAccess, CommonConstants.OctoApiReadOnly])
            .Should().BeTrue();
    }

    // ── The registered policy, evaluated end to end ─────────────────────────

    private static async Task<bool> EvaluateAsync(
        ClaimsPrincipal principal, IReadOnlyCollection<string>? required = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
            McpAuthorizationPolicy.AddMcpTransportPolicy(options, required ?? DefaultRequired));

        var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(
            principal, resource: null, McpAuthorizationPolicy.PolicyName);
        return result.Succeeded;
    }

    [Fact]
    public async Task Policy_AuthenticatedWithFullAccessScope_Succeeds()
    {
        (await EvaluateAsync(Principal(Scope(CommonConstants.OctoApiFullAccess)))).Should().BeTrue();
    }

    [Fact]
    public async Task Policy_AuthenticatedWithoutTheScope_Fails()
    {
        (await EvaluateAsync(Principal(Scope("openid")))).Should().BeFalse();
    }

    [Fact]
    public async Task Policy_UnauthenticatedPrincipalWithTheScope_StillFails()
    {
        // An identity with no authentication type is not authenticated — the policy keeps the
        // RequireAuthenticatedUser() part of RequireAuthorization() it replaces.
        var unauthenticated = new ClaimsPrincipal(
            new ClaimsIdentity([Scope(CommonConstants.OctoApiFullAccess)]));

        (await EvaluateAsync(unauthenticated)).Should().BeFalse();
    }

    [Fact]
    public async Task Policy_EmptyRequirementSet_FallsBackToAuthenticatedOnly()
    {
        // Mcp:RequiredApiScopes cleared = the pre-AB#5032 behaviour, kept reachable as an escape hatch.
        (await EvaluateAsync(Principal(Scope("openid")), [])).Should().BeTrue();
    }

    // ── The shipped default ─────────────────────────────────────────────────

    [Fact]
    public void McpServiceOptions_DefaultsToTheWriteScope()
    {
        new McpServiceOptions().RequiredApiScopes
            .Should().ContainSingle().Which.Should().Be(CommonConstants.OctoApiFullAccess);
    }
}
