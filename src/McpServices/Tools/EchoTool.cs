using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tests
/// </summary>
[McpServerToolType]
public sealed class EchoTool
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    [McpServerTool, Description("Echoes the input back to the client.")]
    public static string Echo(string message)
    {
        return "hello " + message;
    }
}