using System.IO.Pipelines;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#9729: the mandate check as a <c>tools/call</c> filter of the real server (same <see cref="McpServerSetup" /> as
///     the host, MCP over in-memory pipes). An agent's client gets an error result with the refusal code and the tool
///     never runs; a granted tool and every caller that is no agent reach the tool unchanged. "The tool ran" is observed
///     through the tenant resolution the <c>Echo</c> tool calls first.
/// </summary>
public sealed class AgentMandateFilterProtocolTests : IAsyncDisposable
{
    private const string Tenant = "tenant-a";
    private const string AgentClient = "agent-builder";

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly AgentMandateTestTenant _tenant = new(Tenant);
    private readonly Mock<ITenantResolutionService> _tenantResolution = new();
    private readonly DefaultHttpContext _httpContext = new();
    private ServiceProvider? _services;
    private Task? _serverRun;

    public AgentMandateFilterProtocolTests()
    {
        _tenantResolution.Setup(t => t.ResolveTenantId(It.IsAny<string?>())).Returns(Tenant);
        _tenantResolution.Setup(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()))
            .ReturnsAsync(_tenant.Repository.Object);
    }

    private async Task<McpClient> ConnectAsync(ClaimsPrincipal caller)
    {
        _httpContext.User = caller;
        var systemContext = new Mock<ISystemContext>();
        systemContext.Setup(s => s.FindTenantRepositoryAsync(Tenant)).ReturnsAsync(_tenant.Repository.Object);
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(_httpContext);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(accessor.Object);
        services.AddSingleton(systemContext.Object);
        services.AddSingleton(_tenant.CkCache.Object);
        services.AddSingleton(_tenantResolution.Object);
        services.AddSingleton<IToolRiskRegistry, ToolRiskRegistry>();
        services.AddMcpServer(McpServerSetup.ConfigureOptions)
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream())
            .WithOctoCapabilities();
        _services = services.BuildServiceProvider();

        _serverRun = _services.GetRequiredService<McpServer>().RunAsync(_stop.Token);
        var transport = new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ClaimsPrincipal Principal(string? subject, string? clientId, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", Tenant) };
        if (subject != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        // AB#9729: an agent client's token carries the agent scope only; the other callers carry octo_api.
        claims.Add(new Claim("scope", subject == null && clientId == AgentClient ? "octo_agent" : "octo_api"));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static async Task<CallToolResult> EchoAsync(McpClient client) =>
        await client.CallToolAsync("Echo", new Dictionary<string, object?> { ["message"] = "ping" },
            cancellationToken: TestContext.Current.CancellationToken);

    private static JsonElement RefusalOf(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        return JsonDocument.Parse(text).RootElement;
    }

    private void ToolRan(bool expected) =>
        _tenantResolution.Invocations.Any().Should().Be(expected, expected ? "the tool must run" : "the tool must not run");

    [Fact]
    public async Task AgentClient_UngrantedTool_GetsToolNotGranted_AndTheToolDoesNotRun()
    {
        _tenant.AddAgent("Builder", AgentClient, 0, ("query_entities", 0));
        await using var client = await ConnectAsync(Principal(null, AgentClient, "TenantManagement"));

        var refusal = RefusalOf(await EchoAsync(client));

        refusal.GetProperty("errorCode").GetString().Should().Be(AgentMandateGuard.ToolNotGranted);
        refusal.GetProperty("errorMessage").GetString().Should().Contain("Echo").And.Contain("Builder");
        refusal.GetProperty("tool").GetString().Should().Be("Echo");
        ToolRan(false);
    }

    [Fact]
    public async Task AgentClient_GrantedTool_Runs()
    {
        _tenant.AddAgent("Builder", AgentClient, 0, ("Echo", 0));
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        var result = await EchoAsync(client);

        result.IsError.Should().NotBe(true);
        ToolRan(true);
    }

    [Fact]
    public async Task DisabledAgent_GetsAgentDisabled_AndTheToolDoesNotRun()
    {
        var (agent, _) = _tenant.AddAgent("Builder", AgentClient, 0, ("Echo", 0));
        _tenant.SetEnabled(agent, false);
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        RefusalOf(await EchoAsync(client)).GetProperty("errorCode").GetString().Should()
            .Be(AgentMandateGuard.AgentDisabled);
        ToolRan(false);
    }

    [Fact]
    public async Task AgentClient_MediumToolWithLowGrant_GetsRiskAboveCeiling()
    {
        // update_agent is Medium; the grant admits Low only. The refusal comes before the tool's own admin check.
        _tenant.AddAgent("Builder", AgentClient, 2, ("update_agent", 0));
        await using var client = await ConnectAsync(Principal(null, AgentClient, "TenantManagement"));

        var result = await client.CallToolAsync("update_agent",
            new Dictionary<string, object?> { ["agent"] = "Builder", ["changeBudgetPerRun"] = 7 },
            cancellationToken: TestContext.Current.CancellationToken);

        RefusalOf(result).GetProperty("errorCode").GetString().Should().Be(AgentMandateGuard.RiskAboveCeiling);
        ToolRan(false);
    }

    [Fact]
    public async Task StoreUnavailable_AgentLookupFails_IsRefusedFailClosed()
    {
        _tenant.AddAgent("Builder", AgentClient, 0, ("Echo", 0));
        _tenant.ReadFails = true;
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        RefusalOf(await EchoAsync(client)).GetProperty("errorCode").GetString().Should()
            .Be(AgentMandateGuard.MandateUnavailable);
        ToolRan(false);
    }

    [Theory]
    [InlineData("user-1", null)] // a person (Claude Code, octo-cli device login) without client claim
    [InlineData("user-1", "octo-studio")] // a person signed in through Studio
    [InlineData(null, "octo-cli")] // a client-credentials client that is no agent
    public async Task CallerThatIsNoAgent_RunsTheToolUnchanged(string? subject, string? clientId)
    {
        _tenant.AddAgent("Builder", AgentClient, 0, ("query_entities", 0));
        await using var client = await ConnectAsync(Principal(subject, clientId));

        var result = await EchoAsync(client);

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().NotContain(AgentMandateGuard.ToolNotGranted);
        ToolRan(true);
    }

    [Theory]
    [InlineData("knowledge-page://tenant-a/getting-started")]
    [InlineData("ck-schema://tenant-a/domain")]
    public async Task AgentClient_ResourceRead_IsRefusedByTheRegisteredFilter_AndTheResourceDoesNotRun(string uri)
    {
        _tenant.AddAgent("Builder", AgentClient, 2, ("knowledge_*", 2));
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        var act = () => client.ReadResourceAsync(uri, cancellationToken: TestContext.Current.CancellationToken).AsTask();

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should()
            .Contain(AgentMandateGuard.ForbiddenForAgents);
        ToolRan(false);
    }

    // ---- the seeded builder agent (AB#9730) ---------------------------------------------------------------------------

    /// <summary>
    ///     The mandate System.Ai.Builder 1.2.0 seeds (octo-ai-services, seed-data/builder-agent.yaml, AB#9004): 13 grants,
    ///     risk ceiling High, write mode Propose. Kept in sync by hand; the seed test in octo-ai-services pins the same list.
    /// </summary>
    private static readonly (string Pattern, int? MaxRisk)[] BuilderGrants =
    [
        ("get_type_schema", 0), ("search_types", 0), ("get_available_types", 0), ("query_entities", 0),
        ("validate_ck_model", 0), ("import_ck_model", 1), ("get_pipeline_schema", 0),
        ("validate_pipeline_definition", 0), ("dry_run_pipeline", 1), ("deploy_pipeline", 2), ("list_blueprints", 0),
        ("knowledge_search", 0), ("knowledge_get", 0)
    ];

    private void GivenTheSeededBuilder()
    {
        _tenant.WriteMode = 0; // Propose
        _tenant.AddAgent("Builder", AgentClient, 2, BuilderGrants);
    }

    [Theory]
    [InlineData("delete_tenant")]
    [InlineData("create_tenant")]
    [InlineData("create_user")]
    [InlineData("add_client_to_role")]
    public async Task SeededBuilder_IdentityOrTenantTool_GetsToolNotGranted_WithToolAndReason(string tool)
    {
        GivenTheSeededBuilder();
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        var refusal = RefusalOf(await client.CallToolAsync(tool, new Dictionary<string, object?>(),
            cancellationToken: TestContext.Current.CancellationToken));

        refusal.GetProperty("errorCode").GetString().Should().Be(AgentMandateGuard.ToolNotGranted);
        refusal.GetProperty("tool").GetString().Should().Be(tool);
        refusal.GetProperty("errorMessage").GetString().Should().Contain(tool).And.Contain("Builder")
            .And.Contain("grants no such tool");
        ToolRan(false);
    }

    [Theory]
    [InlineData("import_ck_model")]
    [InlineData("deploy_pipeline")]
    [InlineData("dry_run_pipeline")]
    public async Task SeededBuilder_GrantedWritingTool_GetsProposeRequired_AndChangesNothing(string tool)
    {
        GivenTheSeededBuilder();
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        var refusal = RefusalOf(await client.CallToolAsync(tool, new Dictionary<string, object?>(),
            cancellationToken: TestContext.Current.CancellationToken));

        refusal.GetProperty("errorCode").GetString().Should().Be(AgentMandateGuard.ProposeRequired);
        refusal.GetProperty("errorMessage").GetString().Should().Contain("proposal flow not available yet");
        ToolRan(false);
    }

    [Fact]
    public async Task SeededBuilder_GrantedValidationTool_IsNotRefused()
    {
        GivenTheSeededBuilder();
        await using var client = await ConnectAsync(Principal(null, AgentClient));

        var result = await client.CallToolAsync("get_pipeline_schema", new Dictionary<string, object?>(),
            cancellationToken: TestContext.Current.CancellationToken);

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
        text.Should().NotContain(AgentMandateGuard.ToolNotGranted).And.NotContain(AgentMandateGuard.ProposeRequired)
            .And.NotContain(AgentMandateGuard.RiskAboveCeiling);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_serverRun != null)
        {
            try
            {
                await _serverRun;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        if (_services != null)
        {
            await _services.DisposeAsync();
        }

        _stop.Dispose();
    }
}
