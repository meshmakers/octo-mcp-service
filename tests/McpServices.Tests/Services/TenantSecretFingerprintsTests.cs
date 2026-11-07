using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Services.ContentGuard;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#8475: the tenant secret values reach the content guard only as fingerprints, read with the caller's session,
///     and nothing is read without a key ring.
/// </summary>
public class TenantSecretFingerprintsTests : TestBase
{
    private const string Plaintext = "broker-password-8475";
    private readonly Mock<ISecretAttributeProtector> _protector = new();
    private readonly RtEntity _entity;

    public TenantSecretFingerprintsTests()
    {
        var type = CkGraphTestBuilder.BuildType("System.Communication-4.13.0/MqttConfiguration-1",
            ("Name", AttributeValueTypesDto.String, null),
            ("Password", AttributeValueTypesDto.Secret, null));
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([type]);

        _entity = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = type.CkTypeId.ToRtCkId() };
        _entity.SetAttributeRawValue("Password", RtSecretValue.Pending("value-the-protector-mock-reveals"));
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([_entity], 1, null, null));

        _protector.SetupGet(p => p.IsConfigured).Returns(true);
        _protector.Setup(p => p.RevealOrNull(It.IsAny<RtSecretValue?>(), It.IsAny<SecretAccessContext?>()))
            .Returns(Plaintext);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheFingerprintOfEverySecretValue_ReadWithTheCallersSession()
    {
        var fingerprints = await TenantSecretFingerprints.ReadAsync(MockTenantRepository.Object, MockSession.Object,
            MockCkCacheService.Object, _protector.Object, TestContext.Current.CancellationToken);

        fingerprints.Should().BeEquivalentTo([SecretFingerprint.Compute(Plaintext)]);
        fingerprints.Should().NotContain(Plaintext);
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(MockSession.Object,
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()),
            Times.Once);
        _protector.Verify(p => p.RevealOrNull(It.IsAny<RtSecretValue?>(),
            It.Is<SecretAccessContext?>(c => c!.Service == TenantSecretFingerprints.ServiceName &&
                                             c.AttributeName == "Password")), Times.Once);
    }

    [Fact]
    public async Task ReadAsync_WithoutKeyRing_ReadsNothing()
    {
        _protector.SetupGet(p => p.IsConfigured).Returns(false);

        var fingerprints = await TenantSecretFingerprints.ReadAsync(MockTenantRepository.Object, MockSession.Object,
            MockCkCacheService.Object, _protector.Object, TestContext.Current.CancellationToken);

        fingerprints.Should().BeEmpty();
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()),
            Times.Never);
    }

    [Fact]
    public async Task ReadAsync_SkipsShortAndUnreadableValues()
    {
        _protector.Setup(p => p.RevealOrNull(It.IsAny<RtSecretValue?>(), It.IsAny<SecretAccessContext?>()))
            .Returns("short");

        var fingerprints = await TenantSecretFingerprints.ReadAsync(MockTenantRepository.Object, MockSession.Object,
            MockCkCacheService.Object, _protector.Object, TestContext.Current.CancellationToken);

        fingerprints.Should().BeEmpty();
    }

    [Fact]
    public async Task Guard_WithTheReader_RefusesAPageThatQuotesTheTenantSecret()
    {
        var guard = new ContentGuard(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContentGuard>.Instance);
        var context = new ContentGuardContext("test-tenant", "test-subject", "knowledge_upsert_page",
            ct => TenantSecretFingerprints.ReadAsync(MockTenantRepository.Object, MockSession.Object,
                MockCkCacheService.Object, _protector.Object, ct));

        var result = await guard.CheckAsync(context,
            [new ContentGuardField("sections[0]", $"Log in to the broker with {Plaintext} as password.")],
            TestContext.Current.CancellationToken);

        result.Findings.Should().ContainSingle(f => f.Detector == ContentGuardDetector.TenantSecret);
    }
}
