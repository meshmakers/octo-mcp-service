using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions(
    IOptions<McpServiceOptions> mcpServiceOptions,
    IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    : IConfigureNamedOptions<DistributionEventHubOptions>
{
    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.InstancePrefix = mcpServiceOptions.Value.InstancePrefix;
        options.BrokerHost = mcpServiceOptions.Value.BrokerHost;
        options.BrokerUser = mcpServiceOptions.Value.BrokerUser;
        options.BrokerPassword = mcpServiceOptions.Value.BrokerPassword;
        options.RepositoryHost = octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}