using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using ModelContextProtocol.Server;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts;
using System.Runtime.Serialization;
using CkDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace McpServices.Tests;

public abstract class TestBase
{
    protected Mock<McpServer> MockServer { get; private set; }
    protected TestServiceProvider TestServiceProvider { get; private set; }
    protected Mock<IOctoHttpContextAccessor> MockHttpContextAccessor { get; private set; }
    protected Mock<ICkCacheService> MockCkCacheService { get; private set; }
    protected Mock<ITenantRepository> MockTenantRepository { get; private set; }
    protected Mock<IOctoSession> MockSession { get; private set; }

    protected TestBase()
    {
        MockServer = new Mock<McpServer>();
        TestServiceProvider = new TestServiceProvider();
        MockHttpContextAccessor = new Mock<IOctoHttpContextAccessor>();
        MockCkCacheService = new Mock<ICkCacheService>();
        MockTenantRepository = new Mock<ITenantRepository>();
        MockSession = new Mock<IOctoSession>();

        // Setup basic mocks
        MockServer.Setup(s => s.Services).Returns(TestServiceProvider);

        // Register services in test service provider
        TestServiceProvider.RegisterService(MockHttpContextAccessor.Object);
        TestServiceProvider.RegisterService(MockCkCacheService.Object);
        TestServiceProvider.RegisterService<IRtEntityToDtoMapper>(new RtEntityToDtoMapper(MockCkCacheService.Object));

        // Setup HttpContextAccessor
        MockHttpContextAccessor.Setup(h => h.GetTenantRepositoryAsync())
            .ReturnsAsync(MockTenantRepository.Object);

        // Setup TenantRepository
        MockTenantRepository.Setup(tr => tr.TenantId).Returns("test-tenant");
        MockTenantRepository.Setup(tr => tr.GetSessionAsync())
            .ReturnsAsync(MockSession.Object);

        // Setup CkCacheService mocks for RtEntityToDtoMapper
        SetupCkCacheServiceMocks();
    }

    private void SetupCkCacheServiceMocks()
    {
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns((string _, RtCkId<CkTypeId> typeId) => new CkTypeGraph(typeId.FullName, new CkDto.CkCompiledTypeDto()
            {
                TypeId = typeId.ElementId,
                Attributes = new List<CkDto.CkTypeAttributeDto>()
            }));
    }
}