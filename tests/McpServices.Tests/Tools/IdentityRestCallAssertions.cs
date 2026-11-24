using FluentAssertions;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Moq;

namespace McpServices.Tests.Tools;

/// <summary>
///     Checks on the identity REST client that do not name an obsolete member in an expression: the REST writes of
///     <see cref="IIdentityServicesClient" /> are <c>[Obsolete]</c> since octo-sdk AB#9044 (the tools run the
///     <c>System.Identity</c> methods), so the tests check a call by its name instead of with <c>Verify</c>.
/// </summary>
internal static class IdentityRestCallAssertions
{
    /// <summary>The REST client got no call of <paramref name="methodName" /> (any overload).</summary>
    public static void ShouldNotHaveCalled(this Mock<IIdentityServicesClient> client, string methodName) =>
        client.Invocations.Select(i => i.Method.Name).Should().NotContain(methodName);
}
