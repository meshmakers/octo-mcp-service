using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Services;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Identity wave 2: the CK methods address their target by runtime id, the tools by name or client id. These lookups
///     read the runtime id with the existing REST reads (<c>ClientDto.RtId</c>, <c>RoleDto.Id</c>) or, for API scopes and API
///     resources whose REST DTO has no runtime id, with a GraphQL query of the generated DTOs. A target that does not exist
///     throws <see cref="InvalidOperationException" /> (<c>… not found.</c>); a target the identity service returns without a
///     runtime id (an identity service before the wave 2 methods) throws <see cref="RtIdNotResolvedException" />, which the
///     tool reports as a missing method.
/// </summary>
internal static class IdentityRtIds
{
    private const string ApiScopeByNameQuery = """
                                               query apiScopeByName($name: SimpleScalar!) {
                                                 runtime {
                                                   systemIdentityApiScope(
                                                     fieldFilter: [{ attributePath: "name", operator: EQUALS, comparisonValue: $name }]
                                                   ) {
                                                     items { rtId name }
                                                   }
                                                 }
                                               }
                                               """;

    private const string ApiResourceByNameQuery = """
                                                  query apiResourceByName($name: SimpleScalar!) {
                                                    runtime {
                                                      systemIdentityApiResource(
                                                        fieldFilter: [{ attributePath: "name", operator: EQUALS, comparisonValue: $name }]
                                                      ) {
                                                        items { rtId name }
                                                      }
                                                    }
                                                  }
                                                  """;

    /// <summary>The runtime id of the client with the OAuth client id <paramref name="clientId" />.</summary>
    internal static async Task<string> ClientAsync(IdentityClientContext ctx, string clientId)
    {
        var client = await ctx.Client!.GetClient(clientId);
        return Require(client == null, client?.RtId, $"Client '{clientId}'");
    }

    /// <summary>The runtime id of the role named <paramref name="roleName" />.</summary>
    internal static async Task<string> RoleAsync(IdentityClientContext ctx, string roleName)
    {
        var role = await ctx.Client!.GetRoleByName(roleName);
        return Require(role == null, role?.Id, $"Role '{roleName}'");
    }

    /// <summary>The runtime id of the API scope named <paramref name="name" /> (GraphQL query).</summary>
    internal static async Task<string> ApiScopeAsync(IdentityClientContext ctx, string name)
    {
        var result = await ctx.TenantClient!.SendQueryAsync<RtApiScopeDto>(new GraphQLRequest
        {
            Query = ApiScopeByNameQuery,
            OperationName = "apiScopeByName",
            Variables = new { name }
        });
        var scope = result?.Items?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        return Require(scope == null, scope?.RtId.ToString(), $"API scope '{name}'");
    }

    /// <summary>The runtime id of the API resource named <paramref name="name" /> (GraphQL query).</summary>
    internal static async Task<string> ApiResourceAsync(IdentityClientContext ctx, string name)
    {
        var result = await ctx.TenantClient!.SendQueryAsync<RtApiResourceDto>(new GraphQLRequest
        {
            Query = ApiResourceByNameQuery,
            OperationName = "apiResourceByName",
            Variables = new { name }
        });
        var resource = result?.Items?.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        return Require(resource == null, resource?.RtId.ToString(), $"API resource '{name}'");
    }

    /// <summary>
    ///     The methods take role <b>names</b>; the tools accept role ids (and names). Every entry that is the id of a role
    ///     of the tenant becomes its name, any other entry is passed on as a name.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RoleNamesAsync(IdentityClientContext ctx,
        IReadOnlyCollection<string>? roleIdsOrNames)
    {
        if (roleIdsOrNames is not { Count: > 0 })
        {
            return [];
        }

        var roles = (await ctx.Client!.GetRoles()).ToList();
        return roleIdsOrNames
            .Select(entry => roles.FirstOrDefault(r => string.Equals(r.Id, entry, StringComparison.Ordinal))?.Name ?? entry)
            .ToList();
    }

    private static string Require(bool notFound, string? rtId, string target)
    {
        if (notFound)
        {
            throw new InvalidOperationException($"{target} not found.");
        }

        return string.IsNullOrWhiteSpace(rtId)
            ? throw new RtIdNotResolvedException($"The identity service returned {target} without a runtime id.")
            : rtId;
    }
}
