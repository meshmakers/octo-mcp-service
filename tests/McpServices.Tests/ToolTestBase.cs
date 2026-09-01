using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Meshmakers.Octo.Sdk.ServiceClient.ReportingServices;
using Moq;

namespace McpServices.Tests;

/// <summary>
///     Test base for tools that talk to OctoMesh backend services via SDK clients. Pre-registers
///     <see cref="IMcpSessionTokenStore"/> and <see cref="IOctoServiceClientFactory"/> mocks plus convenience
///     helpers to authenticate the current session and bind mocked SDK clients to specific tenants.
/// </summary>
public abstract class ToolTestBase : TestBase
{
    /// <summary>Default tenant used by helpers — matches the value <see cref="TestBase"/> sets up.</summary>
    protected const string DefaultTenantId = "test-tenant";

    /// <summary>Token store mock — by default returns no tokens (unauthenticated).</summary>
    protected Mock<IMcpSessionTokenStore> MockTokenStore { get; }

    /// <summary>Service-client factory mock — by default returns the per-tenant mocks below.</summary>
    protected Mock<IOctoServiceClientFactory> MockClientFactory { get; }

    /// <summary>Cross-tenant token exchanger mock (AB#4338) — by default returns null (exchange fails).</summary>
    protected Mock<ITenantTokenExchanger> MockTokenExchanger { get; }

    /// <summary>Mocked Identity SDK client returned by the factory for the current test.</summary>
    protected Mock<IIdentityServicesClient> MockIdentityClient { get; }

    /// <summary>Mocked Asset SDK client returned by the factory for the current test.</summary>
    protected Mock<IAssetServicesClient> MockAssetClient { get; }

    /// <summary>Mocked Communication SDK client returned by the factory for the current test.</summary>
    protected Mock<ICommunicationServicesClient> MockCommunicationClient { get; }

    /// <summary>Mocked Stream Data SDK client returned by the factory for the current test.</summary>
    protected Mock<IStreamDataServicesClient> MockStreamDataClient { get; }

    /// <summary>Mocked Reporting SDK client returned by the factory for the current test.</summary>
    protected Mock<IReportingServicesClient> MockReportingClient { get; }

    /// <summary>Mocked Bot SDK client returned by the factory for the current test.</summary>
    protected Mock<IBotServicesClient> MockBotClient { get; }

    /// <summary>File-transfer store — real instance, in-memory. Internal because the store type is internal.</summary>
    internal FileTransferStore FileTransferStore { get; }

    protected ToolTestBase()
    {
        MockTokenStore = new Mock<IMcpSessionTokenStore>();
        MockClientFactory = new Mock<IOctoServiceClientFactory>();
        MockTokenExchanger = new Mock<ITenantTokenExchanger>();
        MockIdentityClient = new Mock<IIdentityServicesClient>();
        MockAssetClient = new Mock<IAssetServicesClient>();
        MockCommunicationClient = new Mock<ICommunicationServicesClient>();
        MockStreamDataClient = new Mock<IStreamDataServicesClient>();
        MockReportingClient = new Mock<IReportingServicesClient>();
        MockBotClient = new Mock<IBotServicesClient>();
        FileTransferStore = new FileTransferStore();

        // Factory hands out the per-test mocks regardless of which tenant the tool requests.
        MockClientFactory
            .Setup(f => f.CreateIdentityClient(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(MockIdentityClient.Object);
        MockClientFactory
            .Setup(f => f.CreateAssetClient(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(MockAssetClient.Object);
        MockClientFactory
            .Setup(f => f.CreateCommunicationClient(It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(MockCommunicationClient.Object);
        MockClientFactory
            .Setup(f => f.CreateStreamDataClient(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(MockStreamDataClient.Object);
        MockClientFactory
            .Setup(f => f.CreateReportingClient(It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(MockReportingClient.Object);
        MockClientFactory
            .Setup(f => f.CreateBotClient(It.IsAny<string>()))
            .Returns(MockBotClient.Object);

        // Default cross-tenant exchange (AB#4338): hands back the SAME token for any target tenant. The
        // default session token is a JWT homed in DefaultTenantId (see GivenAuthenticated), so any test
        // that points a tool at another tenant now takes the exchange path — an identity-preserving
        // exchange keeps those tests asserting the token they arranged. Tests that care about the
        // exchange itself override this setup.
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string subjectToken, string _, CancellationToken _) => new McpSessionTokens
            {
                AccessToken = subjectToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        TestServiceProvider.RegisterService(MockTokenStore.Object);
        TestServiceProvider.RegisterService(MockClientFactory.Object);
        TestServiceProvider.RegisterService(MockTokenExchanger.Object);
        TestServiceProvider.RegisterService<IFileTransferStore>(FileTransferStore);
    }

    /// <summary>
    ///     Mark the current MCP session as authenticated and return the access token the store hands out.
    ///     <para>
    ///     The default token is a JWT bound to the request principal <see cref="TestBase" /> installs
    ///     (<c>sub</c> = <see cref="TestBase.DefaultCallerSubjectId" />, <c>tenant_id</c> =
    ///     <see cref="DefaultTenantId" />). Since AB#5036 the session store is only read for the caller it
    ///     belongs to and a stored token must bind back to that principal, so an opaque placeholder string
    ///     would be refused — pin the returned value instead of a literal when a test asserts which token
    ///     reached the SDK factory.
    ///     </para>
    /// </summary>
    /// <param name="accessToken">Explicit token to store; null uses the principal-bound default.</param>
    protected string GivenAuthenticated(string? accessToken = null)
    {
        var effectiveToken = accessToken ?? TestJwt.CreateFull(
            DefaultTenantId, DefaultCallerSubjectId, clientId: null, DefaultCallerRole);

        MockTokenStore
            .Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = effectiveToken,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

        return effectiveToken;
    }

    /// <summary>Mark the current MCP session as unauthenticated (default state).</summary>
    protected void GivenUnauthenticated()
    {
        MockTokenStore
            .Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns((McpSessionTokens?)null);
    }

    /// <summary>Mark the current MCP session token as expired.</summary>
    protected void GivenTokenExpired(string accessToken = "expired-token")
    {
        MockTokenStore
            .Setup(s => s.GetTokens(It.IsAny<string>()))
            .Returns(new McpSessionTokens
            {
                AccessToken = accessToken,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
    }
}
