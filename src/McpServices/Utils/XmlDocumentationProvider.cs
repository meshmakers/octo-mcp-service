using System.Reflection;
using System.Xml.Linq;

namespace Meshmakers.Octo.Backend.McpServices.Utils;

/// <summary>
/// Provider for XML documentation at runtime
/// </summary>
internal class XmlDocumentationProvider
{
    private readonly Dictionary<string, XDocument> _xmlDocs = new();

    public XmlDocumentationProvider()
    {
        LoadXmlDocumentation();
    }

    private void LoadXmlDocumentation()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyName = assembly.GetName().Name;
            var xmlPath = Path.Combine(Path.GetDirectoryName(assembly.Location) ?? "", $"{assemblyName}.xml");

            if (File.Exists(xmlPath))
            {
                var doc = XDocument.Load(xmlPath);
                _xmlDocs[assemblyName!] = doc;
            }
        }
        catch
        {
            // Fallback to empty documentation if the XML file not found
        }
    }

    public string GetMethodSummary(MethodInfo method)
    {
        var memberName = GetMemberName(method);
        var xmlDoc = GetXmlDoc(method.DeclaringType?.Assembly);

        var summaryElement = xmlDoc?.Descendants("member")
            .FirstOrDefault(x => x.Attribute("name")?.Value == memberName)
            ?.Element("summary");

        return summaryElement?.Value.Trim() ?? string.Empty;
    }

    public string GetParameterDescription(MethodInfo method, string parameterName)
    {
        var memberName = GetMemberName(method);
        var xmlDoc = GetXmlDoc(method.DeclaringType?.Assembly);

        var paramElement = xmlDoc?.Descendants("member")
            .FirstOrDefault(x => x.Attribute("name")?.Value == memberName)
            ?.Elements("param")
            .FirstOrDefault(x => x.Attribute("name")?.Value == parameterName);

        return paramElement?.Value.Trim() ?? $"Parameter: {parameterName}";
    }

    public string GetReturnDescription(MethodInfo method)
    {
        var memberName = GetMemberName(method);
        var xmlDoc = GetXmlDoc(method.DeclaringType?.Assembly);

        var returnElement = xmlDoc?.Descendants("member")
            .FirstOrDefault(x => x.Attribute("name")?.Value == memberName)
            ?.Element("returns");

        return returnElement?.Value.Trim() ?? "";
    }

    private string GetMemberName(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var paramString = string.Join(",", parameters.Select(p => p.ParameterType.FullName));
        return $"M:{method.DeclaringType?.FullName}.{method.Name}({paramString})";
    }

    private XDocument? GetXmlDoc(Assembly? assembly)
    {
        if (assembly == null) return null;
        var assemblyName = assembly.GetName().Name;
        return assemblyName != null && _xmlDocs.TryGetValue(assemblyName, out var doc) ? doc : null;
    }
}