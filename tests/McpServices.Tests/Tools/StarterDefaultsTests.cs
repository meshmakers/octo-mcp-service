using FluentAssertions;
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
///     Feature AB#8452 (Kassandra round 1, finding 2): <c>create_tenant starter=true</c> judges the default site and
///     adapter by the communication controller's rule (StarterDeploymentService) - exactly one Cloud site, hosting exactly
///     one workload, and that workload is an adapter - read from the tenant repository.
/// </summary>
public class StarterDefaultsTests
{
    private const int Edge = 0;
    private const int Cloud = 1;

    private readonly List<RtEntity> _sites = [];
    private readonly List<RtEntity> _workloads = [];
    private readonly Mock<ITenantRepository> _repository = new();
    private RtCkId<CkAssociationRoleId>? _role;
    private GraphDirections? _direction;

    public StarterDefaultsTests()
    {
        _repository.Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
                var items = _sites.Where(e => Equals(e.CkTypeId, type)).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
        _repository.Setup(r => r.GetRtAssociationTargetsAsync(It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>(),
                It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtCkId<CkAssociationRoleId>>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<GraphDirections>(), It.IsAny<IReadOnlyList<OctoObjectId>?>(), It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, OctoObjectId _, RtCkId<CkTypeId> _, RtCkId<CkAssociationRoleId> role,
                RtCkId<CkTypeId> _, GraphDirections direction, IReadOnlyList<OctoObjectId>? _, RtEntityQueryOptions _,
                int? _, int? _) =>
            {
                _role = role;
                _direction = direction;
                return new ResultSet<RtEntity>(_workloads.ToList(), _workloads.Count, null, null);
            });
    }

    private static RtEntity Entity(string type, string name, int deploymentState)
    {
        var entity = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = new RtCkId<CkTypeId>(type) };
        entity.SetAttributeValue("Name", AttributeValueTypesDto.String, name);
        entity.SetAttributeValue("DeploymentState", AttributeValueTypesDto.Enum, deploymentState);
        return entity;
    }

    private void GivenSite(int environment, int state = 0)
    {
        var site = Entity(StarterDefaults.SiteType, "default-site", state);
        site.SetAttributeValue("Environment", AttributeValueTypesDto.Enum, environment);
        _sites.Add(site);
    }

    private void GivenWorkload(string type = StarterDefaults.AdapterType, int state = 0) =>
        _workloads.Add(Entity(type, type == StarterDefaults.AdapterType ? "mesh-adapter" : "app", state));

    private Task<StarterDefaultsState> Read() => StarterDefaults.ReadAsync(_repository.Object, Mock.Of<IOctoSession>());

    [Fact]
    public async Task OneCloudSiteWithOneAdapter_IsTheFreshShape_WithItsStates()
    {
        GivenSite(Cloud, state: 0);
        GivenWorkload(state: 1);

        var state = await Read();

        state.IsFreshShape.Should().BeTrue();
        state.Site!.Name.Should().Be("default-site");
        state.Site.IsUndeployed.Should().BeTrue();
        state.Adapter!.Name.Should().Be("mesh-adapter");
        state.Adapter.StateName.Should().Be("Pending");
        _role.Should().Be(new RtCkId<CkAssociationRoleId>(StarterDefaults.HostsRole));
        _direction.Should().Be(GraphDirections.Inbound, "workloads point to their site with Hosts");
    }

    [Fact]
    public async Task EdgeSite_IsNotTheFreshShape()
    {
        GivenSite(Edge);
        GivenWorkload();

        var state = await Read();

        state.IsFreshShape.Should().BeFalse();
        state.ShapeText.Should().Contain("not a Cloud site");
    }

    [Fact]
    public async Task ApplicationNextToTheAdapter_IsNotTheFreshShape()
    {
        GivenSite(Cloud);
        GivenWorkload();
        GivenWorkload("System.Communication/Application");

        var state = await Read();

        state.IsFreshShape.Should().BeFalse();
        state.Adapter.Should().BeNull();
        state.ShapeText.Should().Contain("hosts 2 workload(s)");
    }

    [Fact]
    public async Task OnlyAnApplication_IsNotTheFreshShape()
    {
        GivenSite(Cloud);
        GivenWorkload("System.Communication/Application");

        (await Read()).IsFreshShape.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task NoneOrTwoSites_AreNotTheFreshShape(int sites)
    {
        for (var i = 0; i < sites; i++)
        {
            GivenSite(Cloud);
        }

        var state = await Read();

        state.SiteCount.Should().Be(sites);
        state.IsFreshShape.Should().BeFalse();
        _role.Should().BeNull("the workloads are only read for exactly one site");
    }

    [Fact]
    public async Task MissingEnvironment_IsNotCloud()
    {
        _sites.Add(Entity(StarterDefaults.SiteType, "default-site", 0));
        GivenWorkload();

        (await Read()).IsFreshShape.Should().BeFalse();
    }
}
