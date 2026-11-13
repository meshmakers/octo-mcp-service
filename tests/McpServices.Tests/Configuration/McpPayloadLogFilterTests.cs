using System.ComponentModel;
using System.IO.Pipelines;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
///     AB#8418 — the ModelContextProtocol SDK logs JSON-RPC payloads (tool arguments such as passwords) at Trace. A real
///     in-process MCP server and client exchange a <c>tools/call</c> with a canary password while every log line at Trace
///     level is captured: without <see cref="McpPayloadLogFilter" /> the canary is in the log (so the test can see a leak),
///     with it the canary is in no log line and the SDK still logs its non-payload diagnostics.
/// </summary>
public sealed class McpPayloadLogFilterTests
{
    private readonly string _canary = "canary-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task WithoutTheFilter_TheSdkLogsToolArguments_AtTrace()
    {
        var lines = await CallToolAsync(withFilter: false);

        lines.Should().Contain(l => l.Contains(_canary), "the SDK logs message payloads at Trace; otherwise this test proves nothing");
    }

    [Fact]
    public async Task WithTheFilter_NoLogLineContainsTheToolArguments()
    {
        var lines = await CallToolAsync(withFilter: true);

        lines.Should().NotContain(l => l.Contains(_canary));
        lines.Should().Contain(l => l.StartsWith(McpPayloadLogFilter.SdkCategory),
            "the SDK's diagnostics at Debug and above stay");
    }

    private async Task<List<string>> CallToolAsync(bool withFilter)
    {
        var sink = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(sink);
            if (withFilter)
            {
                builder.AddMcpPayloadLogFilter();
            }
        });

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new McpServerOptions
        {
            ToolCollection = [McpServerTool.Create(Probe, new McpServerToolCreateOptions { Name = "probe" })]
        };
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(),
                loggerFactory: loggerFactory),
            serverOptions, loggerFactory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverRun = server.RunAsync(cts.Token);

        await using (var client = await McpClient.CreateAsync(
                         new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(),
                             loggerFactory),
                         loggerFactory: loggerFactory, cancellationToken: cts.Token))
        {
            var result = await client.CallToolAsync("probe",
                new Dictionary<string, object?> { ["password"] = _canary }, cancellationToken: cts.Token);
            result.IsError.Should().NotBe(true);
        }

        await cts.CancelAsync();
        try
        {
            await serverRun;
        }
        catch (OperationCanceledException)
        {
            // expected: the run ends with the cancellation
        }

        return sink.Lines;
    }

    [Description("Test tool that takes a password and never returns it.")]
    private static string Probe([Description("SENSITIVE: a password.")] string password) => "ok";

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _sync = new();
        private readonly List<string> _lines = [];

        public List<string> Lines
        {
            get
            {
                lock (_sync)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_sync)
            {
                _lines.Add(line);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add($"{category} [{logLevel}] {formatter(state, exception)} {exception}");
        }
    }
}
