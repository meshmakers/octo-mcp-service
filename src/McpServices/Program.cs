// NLog: Set up the logger first to catch all errors

using McpServices.Resources;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Consumers;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Routing;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;
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
        WebRootPath = "wwwroot"
    });

    builder.AddObservability()
        .AddSystemContextHealthCheck();

    // Configure existing options
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<McpServiceOptions>(options =>
        builder.Configuration.GetSection("Mcp").Bind(options));

    // Configure new dynamic tool options
    builder.Services.Configure<DynamicToolOptions>(options =>
        builder.Configuration.GetSection("DynamicTools").Bind(options));

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

    // Add new dynamic tool services
    builder.Services.AddSingleton<IDynamicToolService, DynamicToolService>();
    builder.Services.AddScoped<IToolExecutionService, ToolExecutionService>();
    builder.Services.AddTransient<IRtEntityToDtoMapper, RtEntityToDtoMapper>();

    builder.Services.AddCors();
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            // Make JSON deserialization case-insensitive for better compatibility with MCP clients
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            // Optionally, you can also set the naming policy for serialization
            // options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

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
            c.AddRoutedCommandClient<ExecutePipelineRequest>();

            // c.AddBroadcastEventConsumer<ComControllerAdapterUpdateConsumer, ComControllerAdapterUpdate>();
            // c.AddBroadcastEventConsumer<ComControllerPoolUpdateConsumer, ComControllerPoolUpdate>();
            //
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiFullAccessDisplayName
            },
            {
                CommonConstants.OctoApiReadOnly,
                CommonConstants.OctoApiReadOnlyDisplayName
            }
        };

        options.XmlDocDataTransferObjectAssemblies =
            [typeof(AdapterConfigurationDto).Assembly, typeof(RtEntityId).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = McpTexts.Api_Title;
        options.ApiDescription = McpTexts.Api_Description;

        options.ClientId = CommonConstants.ReportingServicesSwaggerClientId;
        options.AppName = McpTexts.SwaggerClient_Description;
    }).AddVersion();

    // Add MCP services with enhanced tool discovery
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();
    // .WithTools<EchoTool>()
    //  .WithTools<SampleLlmTool>()
    //  .WithTools<ToolManagement>();

    // Add memory caching for better performance
    builder.Services.AddMemoryCache();

    // Add health checks for the new services
    builder.Services.AddHealthChecks()
        .AddCheck<DynamicToolHealthCheck>("dynamic-tools");

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

    app.UseOctoApiVersioningAndDocumentation();

    app.MapObservability();

    // Map MCP endpoint with tenant routing
    app.MapMcp("/{tenantId:tenantId}/mcp");

    // Log startup information
    var dynamicToolOptions = app.Services.GetRequiredService<IOptions<DynamicToolOptions>>().Value;
    logger.Info("OctoMesh MCP Service starting with configuration:");
    logger.Info("- Dynamic tool generation: {Enabled}", dynamicToolOptions.EnableDynamicToolGeneration);
    logger.Info("- Tool statistics: {Enabled}", dynamicToolOptions.EnableToolStatistics);
    logger.Info("- Max query limit: {Limit}", dynamicToolOptions.MaxQueryResultLimit);
    logger.Info("- Preload models: {Models}", string.Join(", ", dynamicToolOptions.PreloadModels));

    app.MapControllerRoute(
        "default",
        "{controller}/{action=Index}/{id?}");

    await app.RunAsync();
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