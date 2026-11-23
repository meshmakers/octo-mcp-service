using System.Text.Json;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Reads the findings of the asset repository's refusal of a CK model source that does not compile
///     (<c>Models/ImportCk</c>: <c>400</c>, problem code <see cref="Code" />). The problem details carry the findings in
///     the extensions <c>errors</c> and <c>warnings</c> in the shape <c>Models/ValidateCk</c> reports them, so
///     <c>import_ck_model</c> returns them like <c>validate_ck_model</c> does. Only what the service answered is read.
/// </summary>
internal static class CkModelCompileFindings
{
    /// <summary>Problem code of the compile refusal.</summary>
    public const string Code = "CK_MODEL_COMPILE_FAILED";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The errors and warnings of a compile refusal in <paramref name="exception" />; <c>false</c> for any other
    ///     exception, another problem code or a body that cannot be read (the caller keeps the plain error text).
    /// </summary>
    public static bool TryRead(Exception exception, out List<CkModelValidationMessageDto> errors,
        out List<CkModelValidationMessageDto> warnings)
    {
        errors = [];
        warnings = [];
        if (exception is not ServiceClientResultException resultException)
        {
            return false;
        }

        var content = resultException.ResponseContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            // Same fallback as the SDK's problem parser: the body follows the status in the message.
            var start = resultException.Message.IndexOf('{');
            if (start < 0)
            {
                return false;
            }

            content = resultException.Message[start..];
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
                code.GetString() != Code)
            {
                return false;
            }

            errors = ReadList(root, "errors");
            warnings = ReadList(root, "warnings");
            return true;
        }
        catch (JsonException)
        {
            errors = [];
            warnings = [];
            return false;
        }
    }

    private static List<CkModelValidationMessageDto> ReadList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.Deserialize<List<CkModelValidationMessageDto>>(Options) ?? []
            : [];
}
