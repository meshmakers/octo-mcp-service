using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

internal class ConfigureOpenIdConnectOptions(IOptions<McpServiceOptions> mcpServiceOptions)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        options.Authority = mcpServiceOptions.Value.AuthorityUrl;
    }
}