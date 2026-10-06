using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#5543: get_secret_inventory over the asset repository secrets overview (handover §7). The HTTP/GraphQL
///     plumbing is covered by <c>RuntimeSecretInventoryClientTests</c>; these tests cover argument validation and
///     the outcome → response mapping.
/// </summary>
public class SecretInventoryToolTests : ToolTestBase
{
    private const string Tenant = "test-tenant";

    private readonly Mock<IRuntimeSecretInventoryClient> _client = new();
    private SecretInventoryRequest? _lastRequest;

    public SecretInventoryToolTests()
    {
        TestServiceProvider.RegisterService(_client.Object);
    }

    private void GivenResult(SecretInventoryQueryResult result)
    {
        _client.Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SecretInventoryRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, SecretInventoryRequest, CancellationToken>((_, _, r, _) => _lastRequest = r)
            .ReturnsAsync(result);
    }

    private static SecretInventoryQueryResult Page(bool hasNext = false, bool withSummary = false) => new()
    {
        Outcome = SecretInventoryQueryOutcome.Succeeded,
        Inventory = new SecretInventoryConnectionInfo
        {
            TotalCount = 3,
            PageInfo = new SecretInventoryPageInfo { HasNextPage = hasNext, EndCursor = hasNext ? "YXJyYXljb25uZWN0aW9uOjE=" : null },
            Items =
            [
                new SecretInventoryItemInfo
                {
                    CkTypeId = "System.Communication/SftpConfiguration", RtId = "507f1f77bcf86cd799439011",
                    AttributePath = "password", AttributeName = "Password", Required = true, Form = "ENC_V2",
                    KeyId = "k2", SetAt = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc)
                },
                new SecretInventoryItemInfo
                {
                    CkTypeId = "System.Ai/AnthropicConfiguration", RtId = "507f1f77bcf86cd799439012",
                    AttributePath = "apiKey", AttributeName = "ApiKey", Required = true, Form = "KEY_MISSING",
                    KeyId = "src1", NeedsReEntry = true
                }
            ]
        },
        Summary = withSummary
            ? new SecretInventorySummaryInfo
            {
                Total = 3, EncV2 = 1, KeyMissing = 1, NotSet = 1, NeedsReEntry = 1,
                EncV2ByKeyId = [new SecretKeyIdCountInfo { KeyId = "k2", Count = 1 }]
            }
            : null
    };

    [Fact]
    public async Task GetSecretInventory_HappyPath_ReturnsPageAndPassesArguments()
    {
        var token = GivenAuthenticated();
        GivenResult(Page(hasNext: true));

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object,
            ckTypeId: " System.Communication/SftpConfiguration ", forms: ["enc_v2", "KeyMissing", "ENC_V2"],
            needsReEntry: true, search: "sftp", first: 25, after: "abc", tenantId: Tenant,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.TenantId.Should().Be(Tenant);
        result.TotalCount.Should().Be(3);
        result.Items.Should().HaveCount(2);
        result.HasNextPage.Should().BeTrue();
        result.EndCursor.Should().Be("YXJyYXljb25uZWN0aW9uOjE=");
        result.Summary.Should().BeNull();
        result.Message.Should().Contain("1 on this page need re-entry").And.Contain("after=");

        _client.Verify(c => c.QueryAsync(token, Tenant, It.IsAny<SecretInventoryRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _lastRequest.Should().NotBeNull();
        _lastRequest!.First.Should().Be(25);
        _lastRequest.After.Should().Be("abc");
        _lastRequest.CkTypeId.Should().Be("System.Communication/SftpConfiguration");
        _lastRequest.Forms.Should().Equal("ENC_V2", "KEY_MISSING");
        _lastRequest.NeedsReEntry.Should().BeTrue();
        _lastRequest.Search.Should().Be("sftp");
        _lastRequest.IncludeSummary.Should().BeFalse();
        _lastRequest.IncludeUsedBy.Should().BeFalse("usedBy is opt-in because it is heavier");
    }

    [Fact]
    public async Task GetSecretInventory_Defaults_First50_NoFilters()
    {
        GivenAuthenticated();
        GivenResult(Page());

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        _lastRequest!.First.Should().Be(SecretMaintenanceTools.InventoryDefaultPageSize);
        _lastRequest.Forms.Should().BeNull();
        _lastRequest.CkTypeId.Should().BeNull();
        _lastRequest.Search.Should().BeNull();
        _lastRequest.NeedsReEntry.Should().BeNull();
    }

    [Fact]
    public async Task GetSecretInventory_SummaryAndUsedBy_AreRequestedAndReturned()
    {
        GivenAuthenticated();
        GivenResult(Page(withSummary: true));

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object, summary: true, usedBy: true,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Summary.Should().NotBeNull();
        result.Summary!.KeyMissing.Should().Be(1);
        result.Message.Should().Contain("keyMissing=1");
        _lastRequest!.IncludeSummary.Should().BeTrue();
        _lastRequest.IncludeUsedBy.Should().BeTrue();
    }

    [Fact]
    public async Task GetSecretInventory_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        _client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task GetSecretInventory_InvalidPageSize_IsRejected(int first)
    {
        GivenAuthenticated();

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object, first: first,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("200");
        _client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ENC_V3")]
    [InlineData("")]
    public async Task GetSecretInventory_UnknownForm_IsRejected(string form)
    {
        GivenAuthenticated();

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object, forms: [form],
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unknown storage form");
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSecretInventory_Forbidden_SurfacesRoleMessage()
    {
        GivenAuthenticated();
        GivenResult(new SecretInventoryQueryResult
        {
            Outcome = SecretInventoryQueryOutcome.Forbidden,
            ErrorMessage = "The secrets overview requires the 'AdminPanelManagement' role in tenant 'test-tenant'."
        });

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.TenantId.Should().Be(Tenant);
        result.ErrorMessage.Should().Contain("AdminPanelManagement");
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSecretInventory_ClientThrows_ReturnsError()
    {
        GivenAuthenticated();
        _client.Setup(c => c.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SecretInventoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await SecretMaintenanceTools.GetSecretInventory(MockServer.Object,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
    }
}
