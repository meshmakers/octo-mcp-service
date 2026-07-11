// NLog: Set up the logger first to catch all errors

using McpServices.Resources;
using Meshmakers.Octo.Backend.McpServices;
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
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
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

    // Endpoint URLs of the OctoMesh backend services used by SDK-based MCP tools.
    builder.Services.Configure<OctoServiceUrlOptions>(options =>
        builder.Configuration.GetSection("OctoServiceUrls").Bind(options));

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

    // Add MCP authentication and tenant resolution services
    builder.Services.AddSingleton<IMcpSessionTokenStore, McpSessionTokenStore>();
    builder.Services.AddSingleton<ISessionTokenRefresher, SessionTokenRefresher>();
    builder.Services.AddSingleton<ITenantTokenExchanger, TenantTokenExchanger>();
    builder.Services.AddTransient<ITenantResolutionService, TenantResolutionService>();
    builder.Services.AddSingleton<IOctoServiceClientFactory, OctoServiceClientFactory>();
    builder.Services.AddSingleton<IFileTransferStore, FileTransferStore>();
    builder.Services.AddSingleton<IToolRiskRegistry, ToolRiskRegistry>();
    builder.Services.AddHostedService<FileTransferSweeper>();
    builder.Services.AddHttpClient("identity")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });
    // #4146 — the named "github" client targets https://api.github.com. No custom cert
    // handler because GitHub's cert chain is in the system trust store on the worker
    // pod and the adapter image. Timeout pinned so a stuck GitHub side never blocks
    // the MCP request slot indefinitely.
    builder.Services.AddHttpClient("github", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
    });
    builder.Services.AddSingleton<IGitHubRepoApiClient, GitHubRepoApiClient>();
    // M3 B-2c-schema-availability — runtime GraphQL introspection client. Uses the
    // existing "identity" named HttpClient (already configured for the self-signed
    // ClusterIP certs that asset-services serves on).
    builder.Services.AddSingleton<IRuntimeGraphqlIntrospectionClient, RuntimeGraphqlIntrospectionClient>();

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
    builder.Services.ConfigureOptions<ConfigureMcpAuthenticationOptions>();
    builder.Services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();

    // AB#4315: actually register the JWT bearer handler + authorization services. The
    // ConfigureJwtBearerOptions above only configures the "Bearer" scheme's options — without
    // AddAuthentication().AddJwtBearer() the scheme was never added, so the MCP transport served
    // tenant data with no authentication at all. The token's Authority + ValidIssuer come from
    // ConfigureJwtBearerOptions.
    //
    // The MCP scheme (.AddMcp) is the default *challenge* scheme: an unauthenticated request to a
    // gated /mcp endpoint answers 401 with a WWW-Authenticate header pointing at the Protected
    // Resource Metadata (RFC 9728), and the handler serves that metadata at
    // /.well-known/oauth-protected-resource. This lets an interactive client (Claude Code) discover
    // the authorization server and log in. Token *validation* stays with the JWT bearer scheme.
    // Metadata content comes from ConfigureMcpAuthenticationOptions.
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer()
        .AddMcp(_ => { });
    builder.Services.AddAuthorization();

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
        .AddMongoDbRuntimeRepository()
        // AB#4232: wire the CrateDB StreamData factory so ITenantContext.GetStreamDataRepository()
        // resolves to a real repository instead of returning null. Without this, every
        // stream_data_* MCP tool surfaces the misleading "Stream data is not enabled for this
        // tenant" error even on tenants where StreamData is fully provisioned and queryable
        // via asset-repo's GraphQL surface. The helm chart's streamdata-env (octo-helm-pro
        // 0.x) already emits OCTO_MCP__STREAMDATA{HOST,USER,PASSWORD} + OCTO_STREAMDATA__ENABLED,
        // bound here via ConfigureMcpStreamDataConfiguration → McpServiceOptions.
        .AddCrateDbStreamDataRepository<ConfigureMcpStreamDataConfiguration>();

    // AB#4232: matches asset-repo-services. Auto-imports System.StreamData (incl. CkRollupArchive)
    // into a tenant the first time EnableStreamData runs; without it the engine falls back to the
    // hardcoded 1.0.0 version (TenantContext.EnsureStreamDataCkModelImportedAsync) which is older
    // than the version every cluster ships today and would refuse the import as a downgrade.
    builder.Services.AddSingleton<Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IStreamDataCkModelDescriptor>(
        _ => new Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.StreamDataCkModelDescriptor(
            Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1.SystemStreamDataCkIds.CkModelId));

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

        options.ClientId = Constants.McpServicesSwaggerClientId;
        options.AppName = McpTexts.SwaggerClient_Description;
    }).AddVersion();

    // Add MCP services with enhanced tool discovery.
    // WithResourcesFromAssembly picks up every [McpServerResourceType] in this assembly
    // (CkSchemaResources, KnowledgeResources — issue #4110) and registers their
    // [McpServerResource]-attributed methods. Worker calls `resources/list` once at session
    // start, then `resources/read` per resource it needs to materialise into CLAUDE.md,
    // replacing the prior pattern of repeated `tools/call get_*` round-trips.
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly();
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

    // AB#4315: authenticate every MCP request. Health/metrics (MapObservability) and the
    // file-transfer endpoints stay anonymous — only the MCP transport below is gated.
    app.UseAuthentication();
    app.UseAuthorization();
    // Validate the route {tenantId} against the token's tenant claim. Client-credentials service
    // tokens without a user 'sub' (AiWorker via IMcpTokenIssuer, mesh-adapter via
    // ServiceAccountConfiguration) are skipped by design — service-to-service access.
    app.UseOctoTenantAuthorization();

    // Map MCP endpoint with tenant routing (existing, backwards compatible)
    app.MapMcp("/{tenantId:tenantId}/mcp")
        .RequireAuthorization();

    // Map tenantless MCP endpoint (tenant resolved via tool parameter)
    app.MapMcp("/mcp")
        .RequireAuthorization();

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