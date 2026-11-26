using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     The MCP server options and capabilities of the service, in one place so that the host (<c>Program</c>) and the
///     protocol tests register exactly the same server.
/// </summary>
internal static class McpServerSetup
{
    /// <summary>
    ///     Sets the server options: the <c>instructions</c> of the <c>initialize</c> result are the fixed knowledge-first
    ///     text (AB#9087, <see cref="KnowledgeFirst.ServerInstructions" />), never tenant data.
    /// </summary>
    public static void ConfigureOptions(McpServerOptions options) =>
        options.ServerInstructions = KnowledgeFirst.ServerInstructions;

    /// <summary>
    ///     Registers every tool, resource and prompt of this assembly: <c>[McpServerToolType]</c>,
    ///     <c>[McpServerResourceType]</c> (CkSchemaResources, KnowledgeResources - issue #4110; the worker calls
    ///     <c>resources/list</c> once at session start, then <c>resources/read</c> per resource it needs) and
    ///     <c>[McpServerPromptType]</c> (KnowledgeFirstPrompts, AB#9087), and the static mandate check in front of every
    ///     <c>tools/call</c> (<see cref="AgentMandateFilter" />, AB#9729): an agent's client runs only the tools its
    ///     mandate grants.
    /// </summary>
    public static IMcpServerBuilder WithOctoCapabilities(this IMcpServerBuilder builder) =>
        builder
            .WithToolsFromAssembly(typeof(McpServerSetup).Assembly)
            .WithResourcesFromAssembly(typeof(McpServerSetup).Assembly)
            .WithPromptsFromAssembly(typeof(McpServerSetup).Assembly)
            .WithRequestFilters(filters => filters.AddCallToolFilter(AgentMandateFilter.Filter));
}
