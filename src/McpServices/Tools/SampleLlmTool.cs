using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// This tool uses dependency injection and async method
/// </summary>
[McpServerToolType]
public sealed class SampleLlmTool
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="thisServer"></param>
    /// <param name="prompt"></param>
    /// <param name="maxTokens"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [McpServerTool(Name = "sampleLLM"), Description("Samples from an LLM using MCP's sampling feature")]
    public static async Task<string> SampleLlm(
        IMcpServer thisServer,
        [Description("The prompt to send to the LLM")] string prompt,
        [Description("Maximum number of tokens to generate")] int maxTokens,
        CancellationToken cancellationToken)
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "You are a helpful test server."),
            new(ChatRole.User, prompt),
        ];

        ChatOptions options = new()
        {
            MaxOutputTokens = maxTokens,
            Temperature = 0.7f,
        };

        var samplingResponse = await thisServer.AsSamplingChatClient().GetResponseAsync(messages, options, cancellationToken);

        return $"LLM sampling result: {samplingResponse}";
    }
}