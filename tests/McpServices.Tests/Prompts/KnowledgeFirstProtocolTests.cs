using System.IO.Pipelines;
using System.Reflection;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Prompts;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace McpServices.Tests.Prompts;

/// <summary>
///     AB#9087: an MCP client connected to the server is told to consult knowledge_search first. The server is registered
///     with the same <see cref="McpServerSetup" /> as the host and talks MCP over in-memory pipes, so the tests see the
///     real <c>initialize</c>, <c>prompts/list</c> and <c>prompts/get</c> responses.
/// </summary>
public sealed class KnowledgeFirstProtocolTests : IAsyncDisposable
{
    private const string Question = "How do I deploy a pipeline to the adapter?";

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _stop = new();
    private Task? _serverRun;

    public KnowledgeFirstProtocolTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer(McpServerSetup.ConfigureOptions)
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream())
            .WithOctoCapabilities();
        _services = services.BuildServiceProvider();
    }

    private async Task<McpClient> ConnectAsync()
    {
        _serverRun = _services.GetRequiredService<McpServer>().RunAsync(_stop.Token);
        var transport = new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Initialize_CarriesTheKnowledgeFirstInstructions_AndTheFiveAreaIndexSlugs()
    {
        await using var client = await ConnectAsync();

        var instructions = client.ServerInstructions;

        instructions.Should().Be(KnowledgeFirst.ServerInstructions);
        instructions.Should().Contain("Call knowledge_search first", Exactly.Once(), "AB#9087: search first")
            .And.Contain("knowledge_get")
            .And.Contain("[admin-index]", "citations by [slug]")
            .And.Contain("answeringConventions")
            .And.Contain("2. Open every page you use with knowledge_get.")
            .And.Contain("4. Follow the answeringRules and the answeringConventions")
            .And.Contain("Cite only pages you opened with knowledge_get for this answer.", "AB#9088: q7/q18")
            .And.Contain("reply with exactly the sentence `No knowledge page answers this question.`, cite no page",
                "AB#9088: q20")
            .And.Contain("System.Ai.Builder", "the fallback for tenants without the builder knowledge");
        foreach (var slug in new[] { "admin-index", "app-index", "pipeline-index", "deploy-index", "diagnose-index" })
        {
            instructions.Should().Contain(slug);
        }
    }

    [Fact]
    public async Task PromptsList_ContainsTheKnowledgeFirstPrompt_WithARequiredQuestion()
    {
        await using var client = await ConnectAsync();

        var prompts = await client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var prompt = prompts.Should().ContainSingle(p => p.Name == "octomesh-knowledge-first").Subject;
        var argument = prompt.ProtocolPrompt.Arguments.Should().ContainSingle().Subject;
        argument.Name.Should().Be("question");
        argument.Required.Should().BeTrue();
    }

    [Fact]
    public async Task PromptsGet_ReturnsTheWrappedSteps_WithTheQuestionFilledIn()
    {
        await using var client = await ConnectAsync();

        var result = await client.GetPromptAsync("octomesh-knowledge-first",
            new Dictionary<string, object?> { ["question"] = Question },
            cancellationToken: TestContext.Current.CancellationToken);

        var message = result.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be(Role.User);
        var text = message.Content.Should().BeOfType<TextContentBlock>().Subject.Text;
        text.Should().Be(KnowledgeFirst.BuildPrompt(Question));
        text.Should().Contain(KnowledgeFirst.Steps).And.Contain(KnowledgeFirst.AreaIndexText)
            .And.Contain(KnowledgeConventions.CitationRule).And.Contain(KnowledgeConventions.NoAnswerRule)
            .And.EndWith("Question: " + Question);
        text.IndexOf("knowledge_search", StringComparison.Ordinal).Should()
            .BeLessThan(text.IndexOf(Question, StringComparison.Ordinal), "the steps wrap the question");
    }

    [Fact]
    public async Task PromptsGet_WithoutAQuestion_IsAnError()
    {
        await using var client = await ConnectAsync();

        var act = () => client.GetPromptAsync("octomesh-knowledge-first",
            new Dictionary<string, object?> { ["question"] = "  " },
            cancellationToken: TestContext.Current.CancellationToken).AsTask();

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public void Instructions_AreACodeConstant_NotTenantData()
    {
        // AB#9087: the text is a compile-time constant (like the answering conventions' default), so no tenant can
        // inject instructions into client sessions; McpServerSetup only copies it into the options.
        var field = typeof(KnowledgeFirst).GetField(nameof(KnowledgeFirst.ServerInstructions),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        field.IsLiteral.Should().BeTrue();
        field.IsInitOnly.Should().BeFalse();

        var options = new McpServerOptions();
        McpServerSetup.ConfigureOptions(options);
        options.ServerInstructions.Should().Be(KnowledgeFirst.ServerInstructions);
    }

    [Fact]
    public void AreaIndexes_AreTheFiveSlugsOfTheInstructions()
    {
        KnowledgeFirst.AreaIndexes.Select(a => a.Slug).Should()
            .Equal("admin-index", "app-index", "pipeline-index", "deploy-index", "diagnose-index");
        foreach (var (slug, title) in KnowledgeFirst.AreaIndexes)
        {
            KnowledgeFirst.ServerInstructions.Should().Contain($"{slug} ({title})");
        }
    }

    [Fact]
    public void Prompt_IsTheSameTextAsTheProtocolResult()
    {
        KnowledgeFirstPrompts.KnowledgeFirstQuestion("  " + Question + " ").Should()
            .Be(KnowledgeFirst.BuildPrompt(Question));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
        if (_serverRun != null)
        {
            try
            {
                await _serverRun;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _services.DisposeAsync();
        _stop.Dispose();
    }
}
