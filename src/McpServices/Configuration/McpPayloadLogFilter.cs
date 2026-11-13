using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     AB#8418: the ModelContextProtocol SDK logs every JSON-RPC message it sends and receives - including the arguments of
///     <c>tools/call</c>, e.g. the password of <c>create_user</c> / <c>reset_user_password</c> - at <c>Trace</c> level
///     (<c>"… transport received message. Message: '{Message}'"</c>). The service sets the minimum level to <c>Trace</c> and
///     lets NLog decide (<c>MinLogLevel</c>, runtime reconfiguration via <c>reconfigureLogLevel</c>), so those lines would
///     reach the log as soon as someone turns on trace logging. This filter drops every log line below <c>Debug</c> from the
///     SDK's categories (<c>ModelContextProtocol</c>, <c>ModelContextProtocol.*</c>) in Microsoft.Extensions.Logging, before
///     NLog sees it - a runtime NLog reconfiguration cannot turn the payload logging back on. The SDK's non-payload
///     diagnostics (Debug and above) stay.
/// </summary>
public static class McpPayloadLogFilter
{
    /// <summary>The logger category prefix of the ModelContextProtocol SDK.</summary>
    public const string SdkCategory = "ModelContextProtocol";

    /// <summary>The lowest level the SDK's categories may log; its message payload lines are <c>Trace</c>.</summary>
    public const LogLevel MinimumSdkLevel = LogLevel.Debug;

    /// <summary>Adds the filter; call it after <c>SetMinimumLevel</c>.</summary>
    public static ILoggingBuilder AddMcpPayloadLogFilter(this ILoggingBuilder builder) =>
        builder.AddFilter(SdkCategory, MinimumSdkLevel);
}
