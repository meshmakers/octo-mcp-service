namespace Meshmakers.Octo.Backend.McpServices.Services.Knowledge;

/// <summary>
///     "Knowledge first" (AB#9087): the fixed text that tells every MCP client connected to OctoMesh to consult the
///     tenant's knowledge base before it answers how to build, administer, deploy or diagnose something on OctoMesh. It
///     goes out as the <c>instructions</c> of the MCP <c>initialize</c> result and as the prompt
///     <see cref="PromptName" />. The text is a code constant, not tenant-writable, for the same reason as the
///     answering conventions (<see cref="KnowledgeConventions" />): a tenant must not inject instructions into every
///     client session.
/// </summary>
internal static class KnowledgeFirst
{
    /// <summary>Name of the MCP prompt that wraps a question with the knowledge-first steps.</summary>
    public const string PromptName = "octomesh-knowledge-first";

    /// <summary>Name of the prompt argument that carries the question.</summary>
    public const string QuestionArgument = "question";

    /// <summary>Value of the page kind <c>Skill</c> (<see cref="KnowledgePages.KindNames" />).</summary>
    public const int SkillKind = 4;

    /// <summary>Name of the opt-in blueprint that seeds the OctoMesh builder knowledge (the Skill pages).</summary>
    public const string BuilderBlueprintName = "System.Ai.Builder";

    /// <summary>The area index pages: one start page per area, slug and title.</summary>
    public static readonly IReadOnlyList<(string Slug, string Title)> AreaIndexes =
    [
        ("admin-index", "Administer a tenant"),
        ("app-index", "Build an app"),
        ("pipeline-index", "Build data pipelines"),
        ("deploy-index", "Deploy and promote"),
        ("diagnose-index", "Diagnose and fix")
    ];

    /// <summary>The steps, shared by the server instructions and the prompt.</summary>
    public const string Steps =
        "1. Call knowledge_search first, with the words of the question, before you answer or call other tools.\n" +
        "2. Open every page you use with knowledge_get.\n" +
        "3. Cite each page you used by its slug in square brackets right after the statement it supports, e.g. " +
        "[admin-index].\n" +
        "4. Follow the answeringRules and the answeringConventions that knowledge_search returns.\n" +
        KnowledgeConventions.CitationRule + "\n" + KnowledgeConventions.ToolRule + "\n" +
        KnowledgeConventions.NoAnswerRule;

    /// <summary>The area index pages as text, shared by the server instructions and the prompt.</summary>
    public const string AreaIndexText =
        "Start pages, one index page per area: admin-index (Administer a tenant), app-index (Build an app), " +
        "pipeline-index (Build data pipelines), deploy-index (Deploy and promote), diagnose-index (Diagnose and fix).";

    /// <summary>The fallback for tenants without the builder knowledge, shared by instructions and prompt.</summary>
    public const string FallbackText =
        "If the tenant has no Skill pages, the OctoMesh builder knowledge (blueprint " + BuilderBlueprintName +
        ") is not installed: knowledge_search then returns builderKnowledgeHint. Tell the user that a tenant " +
        "administrator can install it (opt-in), and answer only from the pages that exist.";

    /// <summary>The <c>instructions</c> of the MCP <c>initialize</c> result.</summary>
    public const string ServerInstructions =
        "OctoMesh knowledge first. For questions on how to build, administer, deploy or diagnose something on " +
        "OctoMesh:\n" + Steps + "\n" + AreaIndexText + "\n" + FallbackText;

    /// <summary>
    ///     <c>builderKnowledgeHint</c> of a <c>knowledge_search</c> result when the tenant has no Skill page.
    /// </summary>
    public const string BuilderKnowledgeHint =
        "This tenant has no Skill pages: the OctoMesh builder knowledge (blueprint " + BuilderBlueprintName +
        ") is not installed. It is opt-in; a tenant administrator installs it with octo-cli `InstallBlueprint -b " +
        BuilderBlueprintName + "-<version>` or the MCP tool install_blueprint (blueprintId '" + BuilderBlueprintName +
        "-<version>'); list_blueprints shows the available versions. Until then answer only from the pages that exist.";

    /// <summary>The prompt text: the question wrapped with the knowledge-first steps.</summary>
    public static string BuildPrompt(string question) =>
        "Answer the following question about OctoMesh from the tenant's knowledge base.\n" + Steps + "\n" +
        AreaIndexText + "\n" + FallbackText + "\n\nQuestion: " + question.Trim();
}
