using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Web;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace McpServices.Tests.Configuration;

/// <summary>
///     AB#8418 (CodeRabbit, octo-mcp-service#74): <c>UseNLog()</c> adds a provider rule for <c>NLogLoggerProvider</c>
///     (<c>RemoveLoggerFactoryFilter</c>, default on). A provider rule wins over a category rule without a provider, so a
///     category-only filter would let the SDK's Trace payloads through to NLog as soon as NLog's own rules allow Trace
///     (<c>OCTO_LOG_LEVEL_ROOT=Trace</c>). The logging is wired exactly like <c>Program.cs</c>, with an NLog memory target at
///     Trace standing in for the console target.
/// </summary>
[Collection(NLogGlobalConfiguration.Name)]
public sealed class McpPayloadLogFilterNLogTests : IDisposable
{
    private readonly LoggingConfiguration? _previous = LogManager.Configuration;

    public void Dispose() => LogManager.Configuration = _previous;

    [Fact]
    public void WiredLikeProgram_NLogAtTrace_GetsNoSdkTraceLine_ButKeepsSdkDebugAndOtherTraceLines()
    {
        var memory = new MemoryTarget("memory") { Layout = "${logger}|${level}|${message}" };
        var config = new LoggingConfiguration();
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memory);
        LogManager.Configuration = config;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddMcpPayloadLogFilter();
        builder.Host.UseNLog();
        using var app = builder.Build();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();

        factory.CreateLogger("ModelContextProtocol.Server.StreamableHttpServerTransport")
            .LogTrace("transport received message. Message: '{Message}'", "payload-canary");
        factory.CreateLogger("ModelContextProtocol.Server.StreamableHttpServerTransport").LogDebug("sdk-debug");
        factory.CreateLogger("Meshmakers.Octo.Backend.McpServices.Other").LogTrace("own-trace");
        LogManager.Flush();

        memory.Logs.Should().NotContain(l => l.Contains("payload-canary"));
        memory.Logs.Should().Contain(l => l.Contains("sdk-debug"));
        memory.Logs.Should().Contain(l => l.Contains("own-trace"), "the filter must not lower the service's own levels");
    }
}

/// <summary>Serialises the tests that replace the process-wide <see cref="LogManager.Configuration" />.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NLogGlobalConfiguration
{
    public const string Name = "NLog global configuration";
}
