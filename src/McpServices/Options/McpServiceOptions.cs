using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Options;

/// <summary>
///     Describes the options for the MCP (Meshmakers Communication Protocol) service
/// </summary>
public class McpServiceOptions
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public McpServiceOptions()
    {
        PublicUrl = "https://localhost:5017";
        AuthorityUrl = "https://localhost:5003";

        BrokerHost = "localhost";
        BrokerVirtualHost = "/";
        BrokerPort = 5672;
        BrokerUser = "guest";
        BrokerPassword = "guest";
#if DEBUGL || DEBUG
        MinLogLevel = LogLevelDto.Trace;
#else
        MinLogLevel = LogLevelDto.Warn;
#endif
    }

    /// <summary>
    ///     (Public) base address of the service
    /// </summary>
    public string PublicUrl { get; set; }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string AuthorityUrl { get; set; }

    /// <summary>
    ///     Gets or sets the prefix for the OctoMesh installation instance.
    /// </summary>
    public string? InstancePrefix { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMQ broker host name
    /// </summary>
    public string BrokerHost { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMQ broker virtual host
    /// </summary>
    public string BrokerVirtualHost { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMQ broker port
    /// </summary>
    public ushort BrokerPort { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMQ broker username
    /// </summary>
    public string? BrokerUser { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMQ broker password
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    ///     Gets or sets the minimal log level to be logged
    /// </summary>
    public LogLevelDto MinLogLevel { get; set; }

    /// <summary>
    ///     Gets or sets the CrateDB host for StreamData queries (e.g. <c>crate-octo-crate.cratedb.svc.cluster.local</c>
    ///     in the cluster, <c>localhost:4301</c> for local dev). Consumed by
    ///     <c>ConfigureMcpStreamDataConfiguration</c> when the root-level <c>StreamData:Enabled</c>
    ///     instance gate is on.
    /// </summary>
    public string? StreamDataHost { get; set; }

    /// <summary>
    ///     Gets or sets the CrateDB user used by the StreamData repository.
    /// </summary>
    public string? StreamDataUser { get; set; }

    /// <summary>
    ///     Gets or sets the CrateDB user's password used by the StreamData repository. Sourced from the
    ///     <c>streamDataPassword</c> backend secret in cluster deployments.
    /// </summary>
    public string? StreamDataPassword { get; set; }

    /// <summary>
    ///     OAuth scopes a token must carry to reach the MCP transport endpoints (AB#5032). A token
    ///     needs <b>one</b> of them; empty means "authenticated is enough", i.e. the pre-AB#5032
    ///     behaviour.
    ///     <para>
    ///     Default is the platform's write scope <c>octo_api</c> only — see
    ///     <c>Configuration/McpAuthorizationPolicy</c> for why the endpoint cannot split read from
    ///     write. Configurable so an operator can widen it for a service account provisioned with a
    ///     different scope without a code change; narrowing below one scope is not meaningful.
    ///     </para>
    /// </summary>
    public IList<string> RequiredApiScopes { get; set; } =
        new List<string> { CommonConstants.OctoApiFullAccess };
}