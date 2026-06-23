using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     Binds the CrateDB connection string for the StreamData repository from
///     <see cref="McpServiceOptions"/> (<c>Mcp:StreamDataHost</c> / <c>StreamDataUser</c> /
///     <c>StreamDataPassword</c>). Registered via
///     <c>.AddCrateDbStreamDataRepository&lt;ConfigureMcpStreamDataConfiguration&gt;()</c>
///     in <c>Program.cs</c> so the McpServices process gains the same query path
///     asset-repo-services uses. Without this configurator, <c>IStreamDataRepositoryFactory</c>
///     is unregistered and every <c>stream_data_*</c> tool surfaces the misleading
///     "Stream data is not enabled for this tenant" error even on tenants where it is.
/// </summary>
public class ConfigureMcpStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
{
    private readonly IOptions<McpServiceOptions> _options;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="options">The MCP service options carrying the StreamData connection details.</param>
    public ConfigureMcpStreamDataConfiguration(IOptions<McpServiceOptions> options)
    {
        _options = options;
    }

    /// <summary>
    ///     Default-name configure (the unnamed registration).
    /// </summary>
    /// <param name="options">The StreamData configuration to populate.</param>
    public void Configure(StreamDataConfiguration options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    /// <summary>
    ///     Named configure path.
    /// </summary>
    /// <param name="name">Options name (default registration only).</param>
    /// <param name="options">The StreamData configuration to populate.</param>
    public void Configure(string? name, StreamDataConfiguration options)
    {
        var o = _options.Value;
        options.ConnectionStringFromConfiguration(
            o.StreamDataHost ?? string.Empty,
            o.StreamDataUser ?? string.Empty,
            o.StreamDataPassword);
    }
}
