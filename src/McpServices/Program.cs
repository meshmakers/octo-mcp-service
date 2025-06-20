

// NLog: Set up the logger first to catch all errors

using Meshmakers.Octo.Backend.McpServices.Tools;
using NLog;
using NLog.Web;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    // Add MCP services
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<EchoTool>()
        .WithTools<SampleLlmTool>();

    builder.Services.AddOpenTelemetry()
        .WithTracing(b => b.AddSource("*")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation())
        .WithMetrics(b => b.AddMeter("*")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation())
        .WithLogging()
        .UseOtlpExporter();

    var app = builder.Build();

    app.MapMcp();

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