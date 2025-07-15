using Moq;
using ModelContextProtocol.Server;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServices.Tests;

public abstract class TestBase
{
    protected Mock<IMcpServer> MockServer { get; private set; }
    protected TestServiceProvider TestServiceProvider { get; private set; }
    protected Mock<IOctoHttpContextAccessor> MockHttpContextAccessor { get; private set; }
    protected Mock<ICkCacheService> MockCkCacheService { get; private set; }
    protected Mock<ITenantRepository> MockTenantRepository { get; private set; }

    protected TestBase()
    {
        MockServer = new Mock<IMcpServer>();
        TestServiceProvider = new TestServiceProvider();
        MockHttpContextAccessor = new Mock<IOctoHttpContextAccessor>();
        MockCkCacheService = new Mock<ICkCacheService>();
        MockTenantRepository = new Mock<ITenantRepository>();

        // Setup basic mocks
        MockServer.Setup(s => s.Services).Returns(TestServiceProvider);
        
        // Register services in test service provider
        TestServiceProvider.RegisterService(MockHttpContextAccessor.Object);
        TestServiceProvider.RegisterService(MockCkCacheService.Object);


        // Setup HttpContextAccessor
        MockHttpContextAccessor.Setup(h => h.GetTenantRepositoryAsync())
            .ReturnsAsync(MockTenantRepository.Object);
        
        // Setup TenantRepository
        MockTenantRepository.Setup(tr => tr.TenantId).Returns("test-tenant");
    }
    
    protected void SetupMockServices()
    {
        // Additional common setup can be added here if needed
    }
}