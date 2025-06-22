// NLog: Set up the logger first to catch all errors

using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Consumers;
using Meshmakers.Octo.Backend.McpServices.Routing;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using ModelContextProtocol.Server;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = Directory.GetCurrentDirectory(),
        WebRootPath = "wwwroot",
    });

    builder.AddObservability()
        .AddSystemContextHealthCheck();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<McpServerOptions>(options =>
        builder.Configuration.GetSection("McpServer").Bind(options));

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    builder.Services
        .AddScopedMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService,
            IConfigurationService>();

    builder.Services.AddCors();
    builder.Services.AddControllers();

    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();
    builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();
    builder.Services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();

    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

    builder.Services.AddOctoServiceInfrastructure("McpServices",
        c =>
        {
            c.AddCommandClient<CreateIdentityDataCommandRequest>(QueueNames.CreateIdentityDataCommand);
            c.AddCommandClient<RemoveRecurringJobsByScheduleGroupRequest>(QueueNames
                .RemoveRecurringJobsByScheduleGroupCommand);
            c.AddRoutedCommandClient<ExecuteMeshPipelineRequest>();

            // c.AddBroadcastEventConsumer<ComControllerAdapterUpdateConsumer, ComControllerAdapterUpdate>();
            // c.AddBroadcastEventConsumer<ComControllerPoolUpdateConsumer, ComControllerPoolUpdate>();
            //
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    // Add MCP services
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();
    // .WithTools<EchoTool>()
    //  .WithTools<SampleLlmTool>()
    //  .WithTools<ToolManagement>();


    // builder.Services.AddOpenTelemetry()
    //     .WithTracing(b => b.AddSource("*")
    //         .AddAspNetCoreInstrumentation()
    //         .AddHttpClientInstrumentation())
    //     .WithMetrics(b => b.AddMeter("*")
    //         .AddAspNetCoreInstrumentation()
    //         .AddHttpClientInstrumentation())
    //     .WithLogging()
    //     .UseOtlpExporter();

    var app = builder.Build();

    app.MapObservability();

    app.MapMcp("/{tenantId:tenantId}/mcp");

    app.Run();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}