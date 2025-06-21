using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

internal class ConfigureJwtBearerOptions(
    IOptions<McpServiceOptions> mcpServiceOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        options.Authority = mcpServiceOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}