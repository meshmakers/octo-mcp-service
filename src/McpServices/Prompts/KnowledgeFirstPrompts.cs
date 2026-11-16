using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Prompts;

/// <summary>
///     MCP prompts of the knowledge base (AB#9087). <c>octomesh-knowledge-first</c> wraps a question with the steps of
///     the server instructions (<see cref="KnowledgeFirst" />): search the knowledge base first, read the pages, cite
///     them by slug and follow the answering conventions.
/// </summary>
[McpServerPromptType]
public static class KnowledgeFirstPrompts
{
    /// <summary>Wraps <paramref name="question" /> with the knowledge-first steps.</summary>
    /// <param name="question">The question about building, administering, deploying or diagnosing on OctoMesh.</param>
    /// <returns>The prompt text, sent as one user message.</returns>
    [McpServerPrompt(Name = KnowledgeFirst.PromptName)]
    [Description(
        "Ask a question about building, administering, deploying or diagnosing on OctoMesh so that it is answered from " +
        "the tenant's knowledge base: knowledge_search first, knowledge_get for the pages, citations by [slug], and " +
        "the answering conventions of the search.")]
    public static string KnowledgeFirstQuestion(
        [Description("The question about OctoMesh.")] string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new McpException($"The argument '{KnowledgeFirst.QuestionArgument}' is required.");
        }

        return KnowledgeFirst.BuildPrompt(question);
    }
}
