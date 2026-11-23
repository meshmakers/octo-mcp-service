using System.Text.Json;
using Json.Schema;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Evaluates a pipeline definition against the compiled composite schema within a budget, for
///     <c>validate_pipeline_definition</c>.
///     <list type="bullet">
///         <item>
///             The verdict comes from the flag output format, which stops at the first failing keyword and keeps no
///             results. Only an invalid definition is evaluated a second time with the list output format, for the
///             findings.
///         </item>
///         <item>
///             A schema that still holds a self-referencing node union after the rewrite of
///             <see cref="PipelineSchemaCompiler" /> is evaluated only for definitions whose node lists nest at most
///             <see cref="MaxDepthForRecursiveUnions" /> levels deep: against such a union the cost grows by about
///             one order of magnitude per nesting level, in every output format. Deeper definitions are refused
///             with <see cref="PipelineDefinitionLimits.RefusedCode" />.
///         </item>
///         <item>
///             At most <see cref="MaxConcurrentEvaluations" /> evaluations run at once in the process; a call that
///             finds no free slot within <see cref="SlotWait" /> is refused with <see cref="BusyCode" />.
///         </item>
///         <item>
///             A call stops waiting for its evaluation after <see cref="Budget" /> and returns
///             <see cref="BudgetExceededCode" />. JsonSchema.Net cannot be cancelled, so the evaluation keeps its slot
///             until it ends; the slot cap bounds how many such evaluations can run.
///         </item>
///     </list>
/// </summary>
internal static class PipelineSchemaEvaluator
{
    /// <summary>Error code when no evaluation slot became free in time.</summary>
    public const string BusyCode = "PIPELINE_VALIDATION_BUSY";

    /// <summary>Error code when the evaluation did not finish within the budget.</summary>
    public const string BudgetExceededCode = "PIPELINE_VALIDATION_BUDGET_EXCEEDED";

    /// <summary>Deepest nesting of node lists evaluated against a schema with a self-referencing node union.</summary>
    public const int MaxDepthForRecursiveUnions = 2;

    /// <summary>Maximum number of schema evaluations running at once in the process.</summary>
    public const int MaxConcurrentEvaluations = 2;

    /// <summary>How long a call waits for a free evaluation slot.</summary>
    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(10);

    /// <summary>How long a call waits for its evaluation to finish.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static readonly SemaphoreSlim Slots = new(MaxConcurrentEvaluations, MaxConcurrentEvaluations);

    /// <summary>The outcome of <see cref="EvaluateAsync" />.</summary>
    /// <param name="ErrorCode">Null when the evaluation completed; otherwise why it did not.</param>
    /// <param name="IsValid">The verdict, when the evaluation completed.</param>
    /// <param name="Findings">The findings of an invalid definition.</param>
    /// <param name="RefusalMessage">
    ///     Why the definition was refused, when <paramref name="ErrorCode" /> is
    ///     <see cref="PipelineDefinitionLimits.RefusedCode" />.
    /// </param>
    public sealed record Outcome(
        string? ErrorCode,
        bool IsValid,
        IReadOnlyList<(string Path, string SchemaPath, string Keyword, string Message)> Findings,
        string? RefusalMessage = null);

    /// <summary>
    ///     Evaluates <paramref name="definition" /> against <paramref name="compiled" /> within the budget.
    /// </summary>
    /// <param name="compiled">The compiled schema.</param>
    /// <param name="definition">The definition as a JSON element.</param>
    /// <param name="nestingDepth">Nesting depth of the definition's node lists (1 = no nested node lists).</param>
    /// <param name="budget">The time budget; <see cref="Budget" /> when null.</param>
    public static async Task<Outcome> EvaluateAsync(
        PipelineSchemaCompiler.CompiledPipelineSchema compiled,
        JsonElement definition,
        int nestingDepth,
        TimeSpan? budget = null)
    {
        if (compiled.RecursiveUnions && nestingDepth > MaxDepthForRecursiveUnions)
        {
            return new Outcome(PipelineDefinitionLimits.RefusedCode, false, [],
                $"The adapter's pipeline schema has a node union this server cannot evaluate in bounded time for " +
                $"node lists nested more than {MaxDepthForRecursiveUnions} levels deep; this definition nests " +
                $"{nestingDepth} levels. Validate the nested parts separately.");
        }

        if (!await Slots.WaitAsync(SlotWait))
        {
            return new Outcome(BusyCode, false, []);
        }

        Task<Outcome> evaluation;
        try
        {
            // A copy the caller cannot dispose while an evaluation that outlived the budget still reads it.
            var element = definition.Clone();
            evaluation = Task.Run(() => Evaluate(compiled, element, nestingDepth));
        }
        catch
        {
            Slots.Release();
            throw;
        }

        // The slot is released when the evaluation ends, not when the caller stops waiting for it.
        _ = evaluation.ContinueWith(task =>
        {
            _ = task.Exception; // observed here when the caller stopped waiting
            Slots.Release();
        }, TaskScheduler.Default);

        try
        {
            return await evaluation.WaitAsync(budget ?? Budget);
        }
        catch (TimeoutException)
        {
            return new Outcome(BudgetExceededCode, false, []);
        }
    }

    private static Outcome Evaluate(
        PipelineSchemaCompiler.CompiledPipelineSchema compiled, JsonElement definition, int nestingDepth)
    {
        var verdict = compiled.Schema.Evaluate(definition, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        if (verdict.IsValid)
        {
            return new Outcome(null, true, []);
        }

        var details = compiled.Schema.Evaluate(definition, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var findings = new List<(string, string, string, string)>();
        foreach (var detail in details.Details ?? [])
        {
            if (detail.Errors is null || IsInsideIf(detail.EvaluationPath.ToString()))
            {
                continue;
            }

            foreach (var (keyword, message) in detail.Errors)
            {
                findings.Add((detail.InstanceLocation.ToString(), detail.EvaluationPath.ToString(), keyword, message));
            }
        }

        return new Outcome(null, false, findings);
    }

    /// <summary>
    ///     True when <paramref name="evaluationPath" /> lies inside an <c>if</c> subschema. A failing <c>if</c> is no
    ///     finding: it only selects whether its <c>then</c> or <c>else</c> applies (the type dispatch of a rewritten
    ///     node union fails the <c>if</c> of every node type but the node's own). A property named <c>if</c>
    ///     (<c>/properties/if</c>) is not the keyword.
    /// </summary>
    internal static bool IsInsideIf(string evaluationPath)
    {
        var segments = evaluationPath.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i] == "if" && segments[i - 1] is not ("properties" or "patternProperties" or "$defs" or
                    "definitions" or "dependentSchemas"))
            {
                return true;
            }
        }

        return false;
    }
}
