using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.McpServices.Models;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Expands a <see cref="CustomAppTemplates" /> string by substituting <c>&lt;&lt;Token&gt;&gt;</c>
///     placeholders. The chevron syntax avoids collision with Angular's <c>{{ }}</c>
///     interpolation in the HTML template. Renders one page at a time — the
///     <c>apply_custom_app_scaffold</c> tool calls the renderer once per template per
///     page in the plan.
/// </summary>
public static class CustomAppTemplateRenderer
{
    private static readonly Regex PlaceholderRegex = new(
        @"<<(?<token>[A-Za-z]+)>>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Substitute every <c>&lt;&lt;Token&gt;&gt;</c> in <paramref name="template" /> with the
    ///     value from <paramref name="values" />. An unknown token throws — better to fail
    ///     loudly in tests than to ship a template with a typo'd placeholder no value
    ///     filled.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        return PlaceholderRegex.Replace(template, match =>
        {
            var token = match.Groups["token"].Value;
            if (!values.TryGetValue(token, out var value))
            {
                throw new InvalidOperationException(
                    $"Template renderer: no value supplied for placeholder <<{token}>>.");
            }
            return value;
        });
    }

    /// <summary>
    ///     Build the per-page placeholder dictionary the renderer feeds into every
    ///     template for one <see cref="ScaffoldedPageInfo" /> page. The binding is
    ///     optional — pages without a binding get TODO stubs in DTO + GraphQL.
    /// </summary>
    public static Dictionary<string, string> BuildValues(
        ScaffoldedPageInfo page,
        ApplyScaffoldTypeBinding? binding)
    {
        var className = page.ClassName;
        var routeSlug = page.RouteSlug;
        var camelClass = char.ToLowerInvariant(className[0]) + className[1..];
        var queryName = "Get" + className;
        var queryFile = "get" + className;
        var modelFile = routeSlug + "-entry";
        var modelName = className + "Entry";
        var typeId = binding?.TypeId ?? "<no type binding supplied>";
        var graphqlOp = binding?.GraphqlOperationName ?? "TODO_OPERATION";
        var attributes = binding?.Attributes ?? new List<ApplyScaffoldAttribute>();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ClassName"] = className,
            ["RouteSlug"] = routeSlug,
            ["CamelClass"] = camelClass,
            ["TypeId"] = typeId,
            ["GraphqlOperation"] = graphqlOp,
            ["QueryName"] = queryName,
            ["QueryFile"] = queryFile,
            ["ModelFile"] = modelFile,
            ["ModelName"] = modelName,
            ["AttributesAsGraphqlLeaves"] = AttributesAsGraphqlLeaves(attributes),
            ["AttributesAsDtoFields"] = AttributesAsDtoFields(attributes, modelName),
            ["AttributesAsMapAssignments"] = AttributesAsMapAssignments(attributes),
        };
    }

    /// <summary>
    ///     Render the attribute list as GraphQL leaf names, indented 10 spaces (under
    ///     <c>node {</c>). Empty list → a single TODO comment so the agent's next-step
    ///     hint has a place to land.
    /// </summary>
    private static string AttributesAsGraphqlLeaves(IReadOnlyList<ApplyScaffoldAttribute> attributes)
    {
        if (attributes.Count == 0)
        {
            return "          # TODO: list the attributes to fetch (one camelCase name per line).";
        }
        var sb = new StringBuilder();
        for (var i = 0; i < attributes.Count; i++)
        {
            sb.Append("          ").Append(attributes[i].Name);
            if (i < attributes.Count - 1)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    ///     Render the attribute list as DTO interface fields, indented 2 spaces.
    ///     Empty list → a single TODO comment.
    /// </summary>
    private static string AttributesAsDtoFields(IReadOnlyList<ApplyScaffoldAttribute> attributes, string modelName)
    {
        if (attributes.Count == 0)
        {
            return $"  // TODO: list the fields of {modelName} — one per attribute on the bound CK type.";
        }
        var sb = new StringBuilder();
        for (var i = 0; i < attributes.Count; i++)
        {
            var a = attributes[i];
            sb.Append("  ").Append(a.Name);
            if (a.IsOptional)
            {
                sb.Append("?: ").Append(a.TsType).Append(" | null;");
            }
            else
            {
                sb.Append(": ").Append(a.TsType).Append(';');
            }
            if (i < attributes.Count - 1)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    ///     Render the attribute list as map-assignments, indented 6 spaces — used inside
    ///     the <c>mapXxxResult</c> function. Empty list → TODO. Optional attributes
    ///     get <c>?? null</c>.
    /// </summary>
    private static string AttributesAsMapAssignments(IReadOnlyList<ApplyScaffoldAttribute> attributes)
    {
        if (attributes.Count == 0)
        {
            return "          // TODO: map the node's fields into the DTO shape.";
        }
        var sb = new StringBuilder();
        for (var i = 0; i < attributes.Count; i++)
        {
            var a = attributes[i];
            sb.Append("          ").Append(a.Name).Append(": node.").Append(a.Name);
            if (a.IsOptional)
            {
                sb.Append(" ?? null,");
            }
            else
            {
                sb.Append(',');
            }
            if (i < attributes.Count - 1)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
