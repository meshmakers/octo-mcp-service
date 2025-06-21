using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoOpenApiOptions(IOptions<McpServiceOptions> octoOptions)
    : IConfigureNamedOptions<OctoOpenApiOptions>
{
    public void Configure(OctoOpenApiOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OctoOpenApiOptions options)
    {
        options.AuthorityUrl = octoOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}
