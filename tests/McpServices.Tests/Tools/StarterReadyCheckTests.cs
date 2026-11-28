using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9573: the ready state of a starter tenant - builder Agent enabled, Mandate with write mode Propose, the agent's
///     service-account client and the knowledge page app-index, as System.Ai.Builder 1.2 seeds them (AB#9004).
/// </summary>
public class StarterReadyCheckTests
{
    private readonly List<RtEntity> _entities = [];
    private readonly Mock<ITenantRepository> _repository = new();

    public StarterReadyCheckTests()
    {
        _repository.Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
                var items = _entities.Where(e => Equals(e.CkTypeId, type)).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
    }

    private RtEntity Add(string ckTypeId, string wellKnownName)
    {
        var entity = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>(ckTypeId),
            RtWellKnownName = wellKnownName
        };
        _entities.Add(entity);
        return entity;
    }

    /// <summary>The seed of System.Ai.Builder 1.2: agent enabled, mandate Propose, client, app-index page.</summary>
    private void GivenTheBuilderSeed(bool agentEnabled = true, int? writeMode = StarterReadyCheck.WriteModePropose)
    {
        Add(StarterReadyCheck.AgentType, StarterReadyCheck.AgentName)
            .SetAttributeValue("Enabled", AttributeValueTypesDto.Boolean, agentEnabled);
        var mandate = Add(StarterReadyCheck.MandateType, StarterReadyCheck.MandateName);
        if (writeMode != null)
        {
            mandate.SetAttributeValue("WriteMode", AttributeValueTypesDto.Enum, writeMode.Value);
        }

        Add(StarterReadyCheck.ClientType, StarterReadyCheck.ClientName);
        Add(KnowledgePages.PageType, StarterReadyCheck.AppIndexSlug);
    }

    private Task<IReadOnlyList<string>> Missing() =>
        StarterReadyCheck.MissingAsync(_repository.Object, Mock.Of<IOctoSession>());

    [Fact]
    public async Task WithTheBuilderSeed_TheTenantIsReady()
    {
        GivenTheBuilderSeed();

        (await Missing()).Should().BeEmpty();
    }

    [Fact]
    public async Task KnowledgePagesOnly_AsWithBuilder11_NamesAgentMandateAndClient()
    {
        Add(KnowledgePages.PageType, StarterReadyCheck.AppIndexSlug);

        var missing = await Missing();

        missing.Should().HaveCount(3);
        missing[0].Should().Contain("builder-agent");
        missing[1].Should().Contain("builder-mandate");
        missing[2].Should().Contain("builder-agent-client");
    }

    [Fact]
    public async Task ADisabledAgent_IsNotReady()
    {
        GivenTheBuilderSeed(agentEnabled: false);

        (await Missing()).Should().ContainSingle().Which.Should().Contain("not enabled");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task AMandateWithoutWriteModePropose_IsNotReady_AlsoWhenTheValueIsMissing(int? writeMode)
    {
        GivenTheBuilderSeed(writeMode: writeMode);

        (await Missing()).Should().ContainSingle().Which.Should().Contain("write mode Propose");
    }

    [Fact]
    public async Task WithoutTheAppIndexPage_IsNotReady()
    {
        GivenTheBuilderSeed();
        _entities.RemoveAll(e => e.RtWellKnownName == StarterReadyCheck.AppIndexSlug);

        (await Missing()).Should().ContainSingle().Which.Should().Contain("app-index");
    }
}
